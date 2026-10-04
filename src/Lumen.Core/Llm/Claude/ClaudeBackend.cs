using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Lumen.Core.Chat;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Lumen.Core.Llm.Claude;

/// <summary>
/// Streams answers from Claude through the official Anthropic .NET SDK.
/// </summary>
/// <remarks>
/// We call the beta Messages endpoint because server-side refusal fallbacks are a beta
/// feature: if Claude's safety classifiers decline a request, the API itself retries it on
/// a recommended fallback model inside the same call, so a false positive does not leave
/// the user without an answer.
/// </remarks>
public sealed class ClaudeBackend : ILlmBackend, IDisposable
{
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Models that accept <c>fallbacks: "default"</c>. Sending it to other models would be rejected.</summary>
    private static readonly string[] FallbackCapableModels = ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    private readonly ISettingsProvider _settings;
    private readonly Func<string?> _apiKeyProvider;
    private readonly ILogger<ClaudeBackend> _logger;
    private readonly Lock _clientGate = new();
    private AnthropicClient? _client;
    private string? _clientKey;

    public ClaudeBackend(ISettingsProvider settings, Func<string?> apiKeyProvider, ILogger<ClaudeBackend> logger)
    {
        _settings = settings;
        _apiKeyProvider = apiKeyProvider;
        _logger = logger;
    }

    public BackendKind Kind => BackendKind.Claude;

    public string DisplayName => _settings.Current.Claude.Model;

    public BackendAvailability CheckAvailability(Conversation conversation) =>
        string.IsNullOrWhiteSpace(_apiKeyProvider())
            ? BackendAvailability.Unavailable("no Anthropic API key is configured")
            : BackendAvailability.Available;

    public async IAsyncEnumerable<string> StreamAsync(
        Conversation conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ClaudeSettings options = _settings.Current.Claude;
        AnthropicClient client = GetClient();
        MessageCreateParams parameters = BuildRequest(conversation, options);

        _logger.LogInformation("Sending {Count} message(s) to {Model}", conversation.Messages.Count, options.Model);

        // C# does not allow 'yield return' inside a try block that has a catch clause, so the
        // enumerator is advanced manually: MoveNextAsync is inside try/catch, the yield is outside.
        IAsyncEnumerator<BetaRawMessageStreamEvent> events =
            client.Beta.Messages.CreateStreaming(parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool producedText = false;
        string? stopReason = null;
        try
        {
            while (true)
            {
                BetaRawMessageStreamEvent streamEvent;
                try
                {
                    if (!await events.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    streamEvent = events.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw Translate(ex, producedText);
                }

                if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var text))
                {
                    if (text.Text.Length > 0)
                    {
                        producedText = true;
                        yield return text.Text;
                    }
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
                {
                    stopReason = reason.ToString();
                }
                else if (streamEvent.TryPickStart(out var start))
                {
                    _logger.LogDebug("Claude stream started, served by {Model}", start.Message.Model);
                }
            }
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("Claude finished with stop reason {StopReason}", stopReason ?? "(none)");

        // Check the stop reason before trusting the content: a refusal is an HTTP 200, not an exception.
        if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
        {
            throw new LlmBackendException(
                BackendKind.Claude,
                producedText
                    ? "Claude stopped partway and declined to continue this request."
                    : "Claude declined this request.",
                canFallBack: false);
        }

        if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
        {
            yield return "\n\n*[Answer cut off: the output token limit in Settings → Claude was reached.]*";
        }
    }

    /// <summary>Builds the request body. Internal so tests can inspect it without network access.</summary>
    internal static MessageCreateParams BuildRequest(Conversation conversation, ClaudeSettings options)
    {
        var messages = new List<BetaMessageParam>(conversation.Messages.Count);
        foreach (ChatMessage message in conversation.Messages)
        {
            messages.Add(ToParam(message));
        }

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxOutputTokens,

            // The system prompt is constant, so mark it cacheable: follow-up questions in the same
            // conversation then re-read it from the prompt cache at a fraction of the input price.
            System = new List<BetaTextBlockParam>
            {
                new() { Text = conversation.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() },
            },
            Messages = messages,
        };

        if (ParseEffort(options.Effort) is { } effort)
        {
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = effort } };
        }

        if (options.UseServerSideFallbacks && FallbackCapableModels.Contains(options.Model, StringComparer.OrdinalIgnoreCase))
        {
            parameters = parameters with { Betas = [ServerSideFallbackBeta], Fallbacks = new Default() };
        }

        return parameters;
    }

    private static BetaMessageParam ToParam(ChatMessage message)
    {
        Role role = message.Role == ChatRole.Assistant ? Role.Assistant : Role.User;
        if (message.Image is null)
        {
            return new BetaMessageParam { Role = role, Content = message.Content };
        }

        // Image first, then the text that refers to it: Claude reads content blocks in order.
        return new BetaMessageParam
        {
            Role = role,
            Content = new List<BetaContentBlockParam>
            {
                new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource { Data = message.Image.ToBase64(), MediaType = MediaType.ImagePng },
                },
                new BetaTextBlockParam { Text = message.Content },
            },
        };
    }

    internal static Effort? ParseEffort(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => null,
    };

    /// <summary>Maps SDK exceptions to user-facing messages, most specific type first.</summary>
    private static LlmBackendException Translate(Exception ex, bool producedText) => ex switch
    {
        AnthropicUnauthorizedException => new LlmBackendException(BackendKind.Claude, "The Anthropic API key was rejected. Check it in Settings → Claude.", canFallBack: true, ex),
        AnthropicForbiddenException => new LlmBackendException(BackendKind.Claude, "This API key is not allowed to use the selected model.", canFallBack: true, ex),
        AnthropicNotFoundException => new LlmBackendException(BackendKind.Claude, "Unknown Claude model. Check the model name in Settings → Claude.", canFallBack: true, ex),
        AnthropicRateLimitException => new LlmBackendException(BackendKind.Claude, "Claude is rate limited right now. Try again in a moment.", canFallBack: true, ex),
        AnthropicBadRequestException => new LlmBackendException(BackendKind.Claude, "Claude rejected the request: " + ex.Message, canFallBack: true, ex),
        Anthropic5xxException => new LlmBackendException(BackendKind.Claude, "The Claude service had a temporary problem. Try again.", canFallBack: true, ex),
        AnthropicIOException => new LlmBackendException(BackendKind.Claude, "Could not reach the Anthropic API. Are you offline?", canFallBack: true, ex),
        AnthropicApiException => new LlmBackendException(BackendKind.Claude, "Claude returned an error: " + ex.Message, canFallBack: true, ex),
        _ => new LlmBackendException(BackendKind.Claude, (producedText ? "The Claude stream broke: " : "Claude request failed: ") + ex.Message, canFallBack: true, ex),
    };

    /// <summary>Reuses one client (and its connection pool) per API key.</summary>
    private AnthropicClient GetClient()
    {
        string apiKey = _apiKeyProvider()
            ?? throw new LlmBackendException(BackendKind.Claude, "No Anthropic API key is configured.", canFallBack: true);

        lock (_clientGate)
        {
            if (_client is null || !string.Equals(_clientKey, apiKey, StringComparison.Ordinal))
            {
                _client?.Dispose();
                _client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 2 };
                _clientKey = apiKey;
            }

            return _client;
        }
    }

    public void Dispose()
    {
        lock (_clientGate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
