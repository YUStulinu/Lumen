using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.Core.Input;

namespace Lumen.App.Controls;

/// <summary>
/// Turns a TextBox into a shortcut recorder: press a key combination and its canonical
/// text ("Ctrl+Shift+Space") appears. Backspace or Delete alone clears it.
/// Usage: <c>&lt;TextBox controls:HotkeyRecorder.IsEnabled="True" Text="{Binding AskHotkey}" /&gt;</c>.
/// </summary>
public static class HotkeyRecorder
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(HotkeyRecorder), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is true)
        {
            box.IsReadOnlyCaretVisible = false;
            box.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;

        // With Alt held, WPF reports Key.System and puts the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            return; // keep Tab for focus navigation
        }

        e.Handled = true;

        if ((key == Key.Back || key == Key.Delete) && modifiers == ModifierKeys.None)
        {
            SetText(box, "");
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || HotkeyGesture.IsModifierKey(vk))
        {
            return; // wait for the non-modifier key
        }

        HotkeyModifiers mods = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            mods |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            mods |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            mods |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            mods |= HotkeyModifiers.Win;
        }

        SetText(box, new HotkeyGesture(mods, vk).ToString());
    }

    private static void SetText(TextBox box, string text)
    {
        box.Text = text;
        box.CaretIndex = text.Length;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
