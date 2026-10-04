namespace Lumen.Core.Chat;

/// <summary>Who authored a message in a conversation.</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>
/// An image attached to a user message (for example the region captured with the snipping overlay).
/// Only the cloud backend can look at images; the local text model receives the OCR text instead.
/// </summary>
/// <param name="PngBytes">The encoded PNG image.</param>
public sealed record ImageAttachment(byte[] PngBytes)
{
    public string ToBase64() => Convert.ToBase64String(PngBytes);
}

/// <summary>
/// One immutable message. Records give us value equality and cheap <c>with</c> copies for free.
/// </summary>
public sealed record ChatMessage(ChatRole Role, string Content, ImageAttachment? Image = null)
{
    public static ChatMessage System(string content) => new(ChatRole.System, content);

    public static ChatMessage User(string content, ImageAttachment? image = null) => new(ChatRole.User, content, image);

    public static ChatMessage Assistant(string content) => new(ChatRole.Assistant, content);
}
