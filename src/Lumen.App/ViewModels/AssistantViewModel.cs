using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Services;
using Lumen.Core.Chat;
using Lumen.Core.Llm;
using Lumen.Core.Prompts;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Lumen.App.ViewModels;

/// <summary>
/// State and behavior of the assistant window. Knows nothing about XAML, so the same logic
/// could drive another UI, and every interaction is an <see cref="System.Windows.Input.ICommand"/>.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject, IDisposable
{
    private readonly LlmRouter _router;
    private readonly ISettingsProvider _settings;
    private readonly ClipboardService _clipboard;
    private readonly ILogger<AssistantViewModel> _logger;

    private CapturedContext _context = CapturedContext.Empty;
    private Conversation? _conversation;
    private CancellationTokenSource? _generation;

    /// <summary>The captured text, editable before running an action.</summary>
    [ObservableProperty]
    private string _capturedText = "";

    [ObservableProperty]
    private string _sourceLabel = "No selection";

    [ObservableProperty]
    private BitmapSource? _screenshotPreview;

    /// <summary>The question / follow-up box at the bottom.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunActionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyAnswerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteAnswerCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Ready";

    public AssistantViewModel(LlmRouter router, ISettingsProvider settings, ClipboardService clipboard, ILogger<AssistantViewModel> logger)
    {
        _router = router;
        _settings = settings;
        _clipboard = clipboard;
        _logger = logger;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConversation));
            CopyAnswerCommand.NotifyCanExecuteChanged();
            PasteAnswerCommand.NotifyCanExecuteChanged();
        };
        RefreshQuickActions();
        _settings.Changed += (_, _) => RefreshQuickActions();
    }

    /// <summary>Asks the window to hide itself (after pasting the answer back, for example).</summary>
    public event EventHandler? HideRequested;

    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

    public ObservableCollection<QuickAction> QuickActions { get; } = [];

    public bool HasConversation => Items.Count > 0;

    public bool HasCapturedContent => !string.IsNullOrWhiteSpace(CapturedText) || ScreenshotPreview is not null;

    /// <summary>True when the answer can be pasted back into the window the text came from.</summary>
    public bool CanPasteBack => _context.TargetWindow != 0;

    /// <summary>Starts a fresh session for newly captured content.</summary>
    public void Load(CapturedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancelGeneration();
        _context = context;
        _conversation = null;
        Items.Clear();
        CapturedText = context.Text;
        SourceLabel = context.Describe();
        ScreenshotPreview = context.Screenshot is null ? null : DecodePreview(context.Screenshot.PngBytes);
        Input = "";
        Status = context.HasText || context.Screenshot is not null
            ? "Choose an action or ask a question"
            : "Nothing selected: ask anything, or paste text above";
        OnPropertyChanged(nameof(HasCapturedContent));
        OnPropertyChanged(nameof(CanPasteBack));
    }

    partial void OnCapturedTextChanged(string value) => OnPropertyChanged(nameof(HasCapturedContent));

    /// <summary>Runs a quick action on the captured text. Always starts a new conversation.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunActionAsync(QuickAction? action)
    {
        if (action is null)
        {
            return;
        }

        _conversation = null;
        Items.Clear();
        await AskAsync(action.Instruction, display: action.Label);
    }

    /// <summary>Sends the typed text: the first question about the capture, or a follow-up.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        string text = Input.Trim();
        Input = "";
        await AskAsync(text, display: text);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => CancelGeneration();

    [RelayCommand(CanExecute = nameof(HasAnswer))]
    private async Task CopyAnswerAsync()
    {
        if (LastAnswer() is { } answer && await ClipboardService.SetTextAsync(answer))
        {
            Status = "Answer copied to the clipboard";
        }
    }

    /// <summary>Replaces the original selection with the answer (great after "Fix writing" or "Translate").</summary>
    [RelayCommand(CanExecute = nameof(HasAnswer))]
    private async Task PasteAnswerAsync()
    {
        if (LastAnswer() is not { } answer)
        {
            return;
        }

        HideRequested?.Invoke(this, EventArgs.Empty);
        if (!await _clipboard.PasteIntoAsync(_context.TargetWindow, answer))
        {
            Status = "The original window is gone; the answer was not pasted";
        }
    }

    [RelayCommand]
    private void NewConversation()
    {
        CancelGeneration();
        _conversation = null;
        Items.Clear();
        Status = "Ready";
    }

    private bool CanStart() => !IsBusy;

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Input);

    private bool HasAnswer() => !IsBusy && LastAnswer() is not null;

    private string? LastAnswer() =>
        Items.LastOrDefault(i => !i.IsUser && !i.IsError && !string.IsNullOrWhiteSpace(i.Text))?.Text;

    /// <summary>
    /// The heart of the window: adds the user's turn, streams the router's events into the
    /// UI, and records the answer so follow-up questions have the full context.
    /// </summary>
    private async Task AskAsync(string instruction, string display)
    {
        if (IsBusy)
        {
            return;
        }

        AppSettings settings = _settings.Current;
        ChatMessage userMessage;
        if (_conversation is null)
        {
            // The user may have edited the captured text in the window; that edit wins.
            CapturedContext context = _context with { Text = CapturedText };
            userMessage = PromptBuilder.BuildFirstMessage(instruction, context, settings.General.PreferredLanguage, settings.Claude.SendScreenshots);
            _conversation = new Conversation(PromptBuilder.SystemPrompt);
        }
        else
        {
            userMessage = ChatMessage.User(instruction);
        }

        _conversation.Add(userMessage);
        Items.Add(new ChatItemViewModel(ChatRole.User, display));
        var answer = new ChatItemViewModel(ChatRole.Assistant) { IsStreaming = true };
        Items.Add(answer);

        IsBusy = true;
        Status = "Thinking…";
        _generation = new CancellationTokenSource();
        var text = new StringBuilder();

        try
        {
            // 'await foreach' resumes on the UI thread after every event (no ConfigureAwait(false)
            // here), so it is safe to update bound properties directly.
            await foreach (RouterEvent routerEvent in _router.StreamAsync(_conversation, _generation.Token))
            {
                switch (routerEvent)
                {
                    case BackendStarted started:
                        answer.BackendLabel = started.DisplayName;
                        answer.Notice = started.Note;
                        Status = $"{started.DisplayName} is answering…";
                        break;

                    case TextDelta delta:
                        text.Append(delta.Text);
                        answer.Text = text.ToString();
                        break;

                    case FallingBack fallback:
                        if (fallback.DiscardPartialOutput)
                        {
                            text.Clear();
                            answer.Text = "";
                        }

                        answer.Notice = $"{fallback.Reason} Switching to the other assistant…";
                        break;

                    case Completed completed:
                        Status = $"{answer.BackendLabel} · {completed.Elapsed.TotalSeconds:F1} s"
                            + (completed.TimeToFirstText is { } first ? $" (first words after {first.TotalSeconds:F1} s)" : "");
                        break;
                }
            }

            _conversation.Add(ChatMessage.Assistant(text.ToString()));
        }
        catch (OperationCanceledException)
        {
            // Keep whatever was written so far; the conversation must still alternate user/assistant.
            if (text.Length > 0)
            {
                _conversation.Add(ChatMessage.Assistant(text + "\n\n[stopped by the user]"));
            }
            else
            {
                _conversation.RemoveTrailingUserMessage();
            }

            answer.Notice = "Stopped.";
            Status = "Stopped";
        }
        catch (LlmBackendException ex)
        {
            _logger.LogWarning(ex, "Request failed");
            _conversation.RemoveTrailingUserMessage();
            answer.IsError = true;
            answer.Text = ex.Message;
            Status = "Failed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while answering");
            _conversation.RemoveTrailingUserMessage();
            answer.IsError = true;
            answer.Text = "Something went wrong: " + ex.Message;
            Status = "Failed";
        }
        finally
        {
            answer.IsStreaming = false;
            _generation?.Dispose();
            _generation = null;
            IsBusy = false;
        }
    }

    private void CancelGeneration()
    {
        try
        {
            _generation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished in the meantime.
        }
    }

    private void RefreshQuickActions()
    {
        QuickActions.Clear();
        foreach (QuickAction action in BuiltInQuickActions.All.Concat(_settings.Current.CustomActions))
        {
            QuickActions.Add(action);
        }
    }

    public void Dispose()
    {
        CancelGeneration();
        _generation?.Dispose();
        _generation = null;
    }

    private static BitmapImage DecodePreview(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // read everything now so the stream can be closed
        image.StreamSource = new MemoryStream(png);
        image.DecodePixelHeight = 240; // a thumbnail is enough; saves memory for large captures
        image.EndInit();
        image.Freeze();
        return image;
    }
}
