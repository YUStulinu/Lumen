using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lumen.Core.Chat;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Lumen.Core.Llm.Local;

/// <summary>
/// Runs a small language model (Phi-4-mini, Qwen2.5, Llama 3.2...) on this PC with ONNX Runtime GenAI.
/// </summary>
/// <remarks>
/// <para>ONNX Runtime GenAI wraps the whole generation loop of a decoder-only transformer:
/// tokenization, the KV cache, sampling and the native inference itself. Our job is to</para>
/// <list type="number">
/// <item>load the model folder once and keep it warm,</item>
/// <item>render the conversation with the right chat template,</item>
/// <item>run "generate next token / decode it" in a loop on a background thread,</item>
/// <item>hand every decoded piece to the UI as soon as it exists.</item>
/// </list>
/// </remarks>
public sealed class OnnxGenAiBackend : ILlmBackend, IDisposable
{
    /// <summary>The answer needs room too: refuse prompts that leave fewer tokens than this.</summary>
    private const int MinimumAnswerTokens = 64;

    private readonly ISettingsProvider _settings;
    private readonly ILogger<OnnxGenAiBackend> _logger;

    // One generation at a time. It also serializes loading and unloading, so the model can
    // never be disposed while a generator is still using it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private LoadedModel? _loaded;
    private bool _disposed;

    public OnnxGenAiBackend(ISettingsProvider settings, ILogger<OnnxGenAiBackend> logger)
    {
        _settings = settings;
        _logger = logger;
        _idleTimer = new Timer(_ => UnloadIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised (on a background thread) when the model is loaded or unloaded.</summary>
    public event EventHandler? StateChanged;

    public BackendKind Kind => BackendKind.Local;

    public string DisplayName => _loaded?.DisplayName ?? DescribeFolder(_settings.Current.Local.ModelPath);

    public bool IsLoaded => _loaded is not null;

    public BackendAvailability CheckAvailability(Conversation conversation)
    {
        string path = _settings.Current.Local.ModelPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return BackendAvailability.Unavailable("no local model folder is configured");
        }

        if (!File.Exists(Path.Combine(path, "genai_config.json")))
        {
            return BackendAvailability.Unavailable($"'{path}' does not contain genai_config.json");
        }

        return BackendAvailability.Available;
    }

    /// <summary>Loads the model ahead of time so the first answer starts immediately.</summary>
    public async Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => EnsureLoaded(_settings.Current.Local), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            RestartIdleTimer();
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        Conversation conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // Cancelled when the consumer stops early (Stop button, window closed, fallback...).
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? producer = null;
        try
        {
            LocalModelSettings options = _settings.Current.Local;
            LoadedModel model = await Task.Run(() => EnsureLoaded(options), cancellationToken).ConfigureAwait(false);
            string prompt = ChatTemplates.Render(model.Format, conversation);

            // Unbounded is fine: the producer emits a few dozen short strings per second at most.
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            producer = Task.Factory.StartNew(
                () => Generate(model, prompt, options, channel.Writer, generationCts.Token),
                generationCts.Token,
                TaskCreationOptions.LongRunning, // a dedicated thread: the loop blocks for seconds
                TaskScheduler.Default);

            await foreach (string piece in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return piece;
            }
        }
        finally
        {
            // Make sure the native generator is gone before another request (or an unload) may touch the model.
            await generationCts.CancelAsync().ConfigureAwait(false);
            if (producer is not null)
            {
                try
                {
                    await producer.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or LlmBackendException)
                {
                    // Already reported to the consumer through the channel.
                }
            }

            _gate.Release();
            RestartIdleTimer();
        }
    }

    /// <summary>The token loop. Runs on its own thread and reports through the channel.</summary>
    private void Generate(LoadedModel model, string prompt, LocalModelSettings options, ChannelWriter<string> writer, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        int generated = 0;
        try
        {
            using Sequences sequences = model.Tokenizer.Encode(prompt);
            int promptTokens = sequences[0].Length;
            int contextLimit = options.MaxContextTokens;
            if (promptTokens + MinimumAnswerTokens > contextLimit)
            {
                throw new ContextTooLongException(promptTokens, contextLimit);
            }

            using var generatorParams = new GeneratorParams(model.Model);
            generatorParams.SetSearchOption("max_length", Math.Min(contextLimit, promptTokens + options.MaxOutputTokens));
            bool sample = options.Temperature > 0.01;
            generatorParams.SetSearchOption("do_sample", sample);
            if (sample)
            {
                generatorParams.SetSearchOption("temperature", options.Temperature);
                generatorParams.SetSearchOption("top_p", options.TopP);
            }

            using var generator = new Generator(model.Model, generatorParams);
            generator.AppendTokenSequences(sequences); // runs the "prefill" pass over the whole prompt

            // A TokenizerStream decodes incrementally: some characters (emoji, ș, ț...) span several
            // tokens, so decoding tokens one by one in isolation would produce garbage.
            using TokenizerStream decoder = model.Tokenizer.CreateStream();
            var stops = new StopSequenceDetector(ChatTemplates.StopMarkers(model.Format));

            while (!generator.IsDone())
            {
                ct.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                int token = generator.GetNextTokens()[0];
                generated++;

                string safe = stops.Push(decoder.Decode(token));
                if (safe.Length > 0)
                {
                    writer.TryWrite(safe);
                }

                if (stops.Stopped)
                {
                    break;
                }
            }

            string rest = stops.Flush();
            if (rest.Length > 0)
            {
                writer.TryWrite(rest);
            }

            double seconds = stopwatch.Elapsed.TotalSeconds;
            _logger.LogInformation(
                "Local generation finished: {Prompt} prompt tokens, {Generated} new tokens in {Seconds:F1}s ({Rate:F1} tok/s)",
                promptTokens, generated, seconds, generated / Math.Max(seconds, 0.001));
            writer.TryComplete();
        }
        catch (OperationCanceledException ex)
        {
            writer.TryComplete(ex);
        }
        catch (LlmBackendException ex)
        {
            writer.TryComplete(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local generation failed after {Generated} tokens", generated);
            writer.TryComplete(new LlmBackendException(BackendKind.Local, "The local model failed: " + ex.Message, canFallBack: true, ex));
        }
    }

    /// <summary>Loads the configured model, or reloads it when the folder or format changed. Caller holds the gate.</summary>
    private LoadedModel EnsureLoaded(LocalModelSettings options)
    {
        string path = Path.GetFullPath(options.ModelPath);
        if (_loaded is not null && string.Equals(_loaded.Path, path, StringComparison.OrdinalIgnoreCase) && _loaded.RequestedFormat == options.PromptFormat)
        {
            return _loaded;
        }

        UnloadCore();

        if (!File.Exists(Path.Combine(path, "genai_config.json")))
        {
            throw new LlmBackendException(BackendKind.Local, $"No ONNX GenAI model found in '{path}'.");
        }

        _logger.LogInformation("Loading local model from {Path}", path);
        var stopwatch = Stopwatch.StartNew();
        Model? model = null;
        try
        {
            // Config reads genai_config.json; the execution provider (CPU, DirectML, CUDA...)
            // comes from that file, so the same code runs whichever package variant is referenced.
            using var config = new Config(path);
            model = new Model(config);
            var tokenizer = new Tokenizer(model);

            string modelType = model.GetModelType();
            PromptFormat format = options.PromptFormat == PromptFormat.Auto
                ? ChatTemplates.DetectFromModelType(modelType)
                : options.PromptFormat;

            _loaded = new LoadedModel(path, model, tokenizer, options.PromptFormat, format, $"{DescribeFolder(path)} (local)");
            _logger.LogInformation(
                "Local model loaded in {Seconds:F1}s: type={Type}, template={Format}",
                stopwatch.Elapsed.TotalSeconds, modelType, format);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return _loaded;
        }
        catch (Exception ex) when (ex is not LlmBackendException)
        {
            model?.Dispose();
            _logger.LogError(ex, "Could not load the local model from {Path}", path);
            throw new LlmBackendException(BackendKind.Local, $"Could not load the local model: {ex.Message}", canFallBack: true, ex);
        }
    }

    private void RestartIdleTimer()
    {
        if (_disposed)
        {
            return;
        }

        int minutes = _settings.Current.Local.UnloadAfterIdleMinutes;
        _idleTimer.Change(minutes > 0 ? TimeSpan.FromMinutes(minutes) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void UnloadIfIdle()
    {
        // Wait(0): if a generation is running right now we are not idle; the timer restarts after it.
        if (_disposed || _loaded is null || !_gate.Wait(0))
        {
            return;
        }

        try
        {
            _logger.LogInformation("Unloading the local model after an idle period");
            UnloadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Frees the model now (for example after the model folder changed in settings).</summary>
    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            UnloadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UnloadCore()
    {
        if (_loaded is null)
        {
            return;
        }

        _loaded.Dispose();
        _loaded = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer.Dispose();
        _gate.Wait();
        try
        {
            UnloadCore();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// "…\Phi-4-mini-instruct-onnx\cpu_and_mobile\cpu-int4-rtn-block-32-acc-level-4" → "Phi-4-mini-instruct-onnx":
    /// model repositories nest one folder per hardware/quantization variant, so skip those.
    /// </summary>
    internal static string DescribeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Local model";
        }

        string[] variantPrefixes = ["cpu", "gpu", "cuda", "dml", "directml", "npu", "int4", "int8", "fp16", "fp32", "mobile"];
        DirectoryInfo dir = new(path.TrimEnd('\\', '/'));
        for (int depth = 0; depth < 3 && dir.Parent is not null; depth++)
        {
            bool isVariantFolder = variantPrefixes.Any(p => dir.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (!isVariantFolder)
            {
                break;
            }

            dir = dir.Parent;
        }

        return dir.Name;
    }

    private sealed record LoadedModel(
        string Path,
        Model Model,
        Tokenizer Tokenizer,
        PromptFormat RequestedFormat,
        PromptFormat Format,
        string DisplayName) : IDisposable
    {
        public void Dispose()
        {
            // Dispose in reverse order of creation: the tokenizer references the model.
            Tokenizer.Dispose();
            Model.Dispose();
        }
    }
}
