using Lumen.Core.Chat;

namespace Lumen.Core.Llm;

/// <summary>The two kinds of engine Lumen can talk to.</summary>
public enum BackendKind
{
    /// <summary>A model running on this PC through ONNX Runtime GenAI.</summary>
    Local,

    /// <summary>Claude, through the Anthropic API.</summary>
    Claude,
}

/// <summary>Result of a cheap, side-effect free "could this backend serve a request right now?" check.</summary>
public sealed record BackendAvailability(bool IsAvailable, string? Reason = null)
{
    public static BackendAvailability Available { get; } = new(true);

    public static BackendAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>
/// A text generation engine. Implementations stream the answer as it is produced.
/// </summary>
/// <remarks>
/// <see cref="IAsyncEnumerable{T}"/> is the natural shape for token streaming in C#:
/// the caller writes <c>await foreach (var piece in backend.StreamAsync(...))</c>,
/// every piece arrives as soon as the model emits it, and cancellation flows
/// through the <see cref="CancellationToken"/> into the producer.
/// </remarks>
public interface ILlmBackend
{
    BackendKind Kind { get; }

    /// <summary>Human readable name shown in the UI, e.g. "Phi-4-mini (local)" or "claude-opus-5-5".</summary>
    string DisplayName { get; }

    /// <summary>Whether the backend can be used without doing any expensive work (no model load, no network).</summary>
    BackendAvailability CheckAvailability(Conversation conversation);

    /// <summary>Streams the assistant's reply to the last user message of <paramref name="conversation"/>.</summary>
    /// <exception cref="LlmBackendException">The backend failed; see <see cref="LlmBackendException.FailedBeforeOutput"/>.</exception>
    IAsyncEnumerable<string> StreamAsync(Conversation conversation, CancellationToken cancellationToken);
}
