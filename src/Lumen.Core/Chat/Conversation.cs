namespace Lumen.Core.Chat;

/// <summary>
/// An ordered, append-only list of messages plus the system prompt that frames them.
/// </summary>
/// <remarks>
/// The conversation is append-only on purpose: the Claude API caches prompt prefixes and
/// newer models validate that earlier turns were not edited, so we never rewrite history.
/// A follow-up question simply appends a user message and, later, the assistant's answer.
/// </remarks>
public sealed class Conversation
{
    private readonly List<ChatMessage> _messages = [];

    public Conversation(string systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        SystemPrompt = systemPrompt;
    }

    public string SystemPrompt { get; }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>True when at least one message carries an image (only the cloud backend can see it).</summary>
    public bool HasImages => _messages.Any(m => m.Image is not null);

    public void Add(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role == ChatRole.System)
        {
            throw new ArgumentException("System instructions belong in SystemPrompt, not in the message list.", nameof(message));
        }

        if (_messages.Count > 0 && _messages[^1].Role == message.Role)
        {
            // Both backends expect strictly alternating user/assistant turns.
            throw new InvalidOperationException($"Two consecutive '{message.Role}' messages are not allowed.");
        }

        _messages.Add(message);
    }

    /// <summary>
    /// Drops the trailing user message. Used when a request fails before the assistant
    /// answered, so the user can retry without leaving an orphaned turn behind.
    /// </summary>
    public void RemoveTrailingUserMessage()
    {
        if (_messages.Count > 0 && _messages[^1].Role == ChatRole.User)
        {
            _messages.RemoveAt(_messages.Count - 1);
        }
    }
}
