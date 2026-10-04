namespace Lumen.Core.Llm;

/// <summary>
/// Raised by a backend when it cannot produce an answer. The router uses the extra
/// information to decide whether trying the other backend makes sense.
/// </summary>
public class LlmBackendException : Exception
{
    public LlmBackendException()
    {
    }

    public LlmBackendException(string message)
        : base(message)
    {
    }

    public LlmBackendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LlmBackendException(BackendKind backend, string message, bool canFallBack = true, Exception? innerException = null)
        : base(message, innerException)
    {
        Backend = backend;
        CanFallBack = canFallBack;
    }

    public BackendKind Backend { get; }

    /// <summary>
    /// False when retrying elsewhere would not help or would be wrong
    /// (for example Claude declined the request for policy reasons).
    /// </summary>
    public bool CanFallBack { get; } = true;
}

/// <summary>The prompt does not fit in the local model's context window.</summary>
public sealed class ContextTooLongException : LlmBackendException
{
    public ContextTooLongException()
    {
    }

    public ContextTooLongException(string message)
        : base(message)
    {
    }

    public ContextTooLongException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ContextTooLongException(int promptTokens, int contextLimit)
        : base(BackendKind.Local, $"The prompt has {promptTokens} tokens but the local model is limited to {contextLimit}.")
    {
        PromptTokens = promptTokens;
        ContextLimit = contextLimit;
    }

    public int PromptTokens { get; }

    public int ContextLimit { get; }
}
