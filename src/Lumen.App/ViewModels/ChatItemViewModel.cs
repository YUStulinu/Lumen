using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.Core.Chat;

namespace Lumen.App.ViewModels;

/// <summary>One bubble in the conversation view.</summary>
/// <remarks>
/// <c>[ObservableProperty]</c> on a field makes the CommunityToolkit.Mvvm source generator write
/// the public property, including the INotifyPropertyChanged plumbing, at compile time.
/// <c>private string _text</c> becomes <c>public string Text { get; set; }</c> that notifies bindings.
/// </remarks>
public sealed partial class ChatItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = "";

    /// <summary>Which engine wrote the answer, e.g. "claude-opus-5-5".</summary>
    [ObservableProperty]
    private string? _backendLabel;

    /// <summary>A small line above the answer, e.g. why the router fell back to Claude.</summary>
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isError;

    public ChatItemViewModel(ChatRole role, string text = "")
    {
        Role = role;
        _text = text;
    }

    public ChatRole Role { get; }

    public bool IsUser => Role == ChatRole.User;
}
