using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lumen.Core.Chat;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Lumen.Core.Llm;

/// <summary>Something the router wants the UI to know while an answer is being produced.</summary>
public abstract record RouterEvent;

/// <summary>A backend starts answering. <paramref name="Note"/> explains why an earlier choice was skipped, if one was.</summary>
public sealed record BackendStarted(BackendKind Backend, string DisplayName, string? Note) : RouterEvent;

/// <summary>A new piece of the answer.</summary>
public sealed record TextDelta(string Text) : RouterEvent;

/// <summary>
/// The current backend failed and another one will try.
/// When <paramref name="DiscardPartialOutput"/> is true the UI must clear the text shown so far.
/// </summary>
public sealed record FallingBack(BackendKind From, string Reason, bool DiscardPartialOutput) : RouterEvent;

/// <summary>The answer is complete.</summary>
public sealed record Completed(BackendKind Backend, TimeSpan Elapsed, TimeSpan? TimeToFirstText, int Characters) : RouterEvent;

/// <summary>
/// Chooses a backend for every request and falls back to the other one when the first fails.
/// </summary>
/// <remarks>
/// The policy, in order:
/// <list type="number">
/// <item>The routing mode gives a preference order (e.g. LocalFirst = [Local, Claude]).</item>
/// <item>Backends that are not available (no model folder, no API key) are skipped.</item>
/// <item>The local model is skipped when the prompt obviously exceeds its context window
///       and another backend could take it.</item>
/// <item>If the chosen backend fails with a recoverable error, the next one is tried.
///       Cancellation by the user is never treated as a failure.</item>
/// </list>
/// </remarks>
public sealed class LlmRouter
{
    private readonly Dictionary<BackendKind, ILlmBackend> _backends;
    private readonly ISettingsProvider _settings;
    private readonly ILogger<LlmRouter> _logger;

    public LlmRouter(IEnumerable<ILlmBackend> backends, ISettingsProvider settings, ILogger<LlmRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.ToDictionary(b => b.Kind);
        _settings = settings;
        _logger = logger;
    }

    public static IReadOnlyList<BackendKind> PreferenceOrder(RoutingMode mode) => mode switch
    {
        RoutingMode.LocalFirst => [BackendKind.Local, BackendKind.Claude],
        RoutingMode.CloudFirst => [BackendKind.Claude, BackendKind.Local],
        RoutingMode.LocalOnly => [BackendKind.Local],
        RoutingMode.CloudOnly => [BackendKind.Claude],
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>A deliberately pessimistic token estimate (~3 characters per token covers non-English text too).</summary>
    public static int EstimateTokens(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        int chars = conversation.SystemPrompt.Length + conversation.Messages.Sum(m => m.Content.Length + 8);
        return chars / 3;
    }

    public async IAsyncEnumerable<RouterEvent> StreamAsync(
        Conversation conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        AppSettings settings = _settings.Current;
        List<ILlmBackend> candidates = PreferenceOrder(settings.RoutingMode)
            .Where(_backends.ContainsKey)
            .Select(k => _backends[k])
            .ToList();

        var notes = new List<string>();
        LlmBackendException? lastFailure = null;

        for (int i = 0; i < candidates.Count; i++)
        {
            ILlmBackend backend = candidates[i];
            // Only worth catching a failure if some later backend could actually take over.
            bool hasAlternative = candidates.Skip(i + 1).Any(c => c.CheckAvailability(conversation).IsAvailable);

            BackendAvailability availability = backend.CheckAvailability(conversation);
            if (!availability.IsAvailable)
            {
                notes.Add($"{Name(backend.Kind)} skipped: {availability.Reason}");
                continue;
            }

            if (backend.Kind == BackendKind.Local && hasAlternative && EstimateTokens(conversation) > settings.Local.MaxContextTokens)
            {
                notes.Add($"{Name(backend.Kind)} skipped: the text is too long for its {settings.Local.MaxContextTokens}-token context");
                continue;
            }

            string? note = notes.Count > 0 ? string.Join("; ", notes) : null;
            _logger.LogInformation("Routing request to {Backend}{Note}", backend.Kind, note is null ? "" : $" ({note})");
            yield return new BackendStarted(backend.Kind, backend.DisplayName, note);

            var stopwatch = Stopwatch.StartNew();
            TimeSpan? firstText = null;
            int characters = 0;
            LlmBackendException? failure = null;

            await using (IAsyncEnumerator<string> pieces = backend.StreamAsync(conversation, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    try
                    {
                        if (!await pieces.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (LlmBackendException ex) when (ex.CanFallBack && hasAlternative)
                    {
                        failure = ex;
                        break;
                    }

                    firstText ??= stopwatch.Elapsed;
                    characters += pieces.Current.Length;
                    yield return new TextDelta(pieces.Current);
                }
            }

            if (failure is null)
            {
                yield return new Completed(backend.Kind, stopwatch.Elapsed, firstText, characters);
                yield break;
            }

            _logger.LogWarning(failure, "{Backend} failed; trying the next backend", backend.Kind);
            lastFailure = failure;
            notes.Clear();
            notes.Add($"{Name(backend.Kind)} failed: {failure.Message}");
            yield return new FallingBack(backend.Kind, failure.Message, DiscardPartialOutput: characters > 0);
        }

        // Nothing could answer. Surface the most useful explanation.
        if (lastFailure is not null)
        {
            throw lastFailure;
        }

        throw new LlmBackendException(
            candidates.Count == 0 ? BackendKind.Local : candidates[0].Kind,
            "No assistant is available: " + string.Join("; ", notes) + ". Open Settings to configure a local model or a Claude API key.",
            canFallBack: false);
    }

    private static string Name(BackendKind kind) => kind == BackendKind.Local ? "Local model" : "Claude";
}
