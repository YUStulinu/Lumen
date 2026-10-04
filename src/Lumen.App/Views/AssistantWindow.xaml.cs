using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.App.Infrastructure;
using Lumen.App.ViewModels;
using Lumen.Core.Settings;

namespace Lumen.App.Views;

/// <summary>
/// The assistant window. Behavior lives in <see cref="AssistantViewModel"/>; this code-behind
/// only handles things that are purely visual: placement, focus, keys and auto-scrolling.
/// </summary>
public partial class AssistantWindow : Window
{
    private readonly AssistantViewModel _viewModel;
    private readonly ISettingsProvider _settings;
    private bool _autoScroll = true;

    public AssistantWindow(AssistantViewModel viewModel, ISettingsProvider settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        SourceInitialized += (_, _) => WindowHelpers.ApplySystemChrome(this);
        Deactivated += OnDeactivated;
        viewModel.HideRequested += (_, _) => Hide();
        ApplySettings(settings.Current);
        settings.Changed += (_, s) => Dispatcher.Invoke(() => ApplySettings(s));
    }

    /// <summary>Raised when the user clicks the gear button.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Shows the window next to the mouse cursor and focuses the most useful control.</summary>
    public void ShowNearCursor()
    {
        if (!IsVisible)
        {
            WindowHelpers.MoveNearCursor(this);
        }

        WindowHelpers.ShowAndActivate(this);
        _autoScroll = true;

        // With captured text the next step is choosing an action (Ctrl+1..9) or typing a question.
        InputBox.Focus();
    }

    private void ApplySettings(AppSettings settings) => Topmost = settings.General.AlwaysOnTop;

    /// <summary>Closing only hides the window: the app keeps running in the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsBusy)
            {
                _viewModel.StopCommand.Execute(null);
            }
            else
            {
                Hide();
            }

            e.Handled = true;
            return;
        }

        // Ctrl+1..9 runs the matching quick action.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9)
        {
            int index = e.Key - Key.D1;
            if (index < _viewModel.QuickActions.Count && _viewModel.RunActionCommand.CanExecute(_viewModel.QuickActions[index]))
            {
                _viewModel.RunActionCommand.Execute(_viewModel.QuickActions[index]);
            }

            e.Handled = true;
        }
    }

    /// <summary>Enter sends; Shift+Enter inserts a new line.</summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int caret = InputBox.CaretIndex;
            InputBox.Text = InputBox.Text.Insert(caret, Environment.NewLine);
            InputBox.CaretIndex = caret + Environment.NewLine.Length;
        }
        else if (_viewModel.SendCommand.CanExecute(null))
        {
            _viewModel.SendCommand.Execute(null);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Follows the answer as it grows, unless the user scrolled up to read something:
    /// then we leave the scroll position alone until they come back to the bottom.
    /// </summary>
    private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            // The user scrolled.
            _autoScroll = ConversationScroll.VerticalOffset >= ConversationScroll.ScrollableHeight - 4;
        }
        else if (_autoScroll)
        {
            // The content grew.
            ConversationScroll.ScrollToVerticalOffset(ConversationScroll.ExtentHeight);
        }
    }

    /// <summary>
    /// Each answer is a RichTextBox with its own inner ScrollViewer, which would swallow the
    /// mouse wheel. Handling the wheel in the Preview (tunneling) phase, before it reaches
    /// them, keeps the whole conversation scrolling smoothly wherever the cursor is.
    /// </summary>
    private void OnConversationMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ConversationScroll.ScrollToVerticalOffset(ConversationScroll.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Like a launcher: optionally disappear when the user clicks elsewhere.
        if (_settings.Current.General.HideOnDeactivate && !_viewModel.IsBusy && OwnedWindows.Count == 0)
        {
            Hide();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
