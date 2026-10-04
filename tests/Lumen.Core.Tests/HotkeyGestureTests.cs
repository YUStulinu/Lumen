using Lumen.Core.Input;

namespace Lumen.Core.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Space", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x20u)]
    [InlineData("ctrl + alt + s", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x53u)]
    [InlineData("Win+Shift+F1", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x70u)]
    [InlineData("Control+D5", HotkeyModifiers.Control, 0x35u)]
    [InlineData("Alt+PageDown", HotkeyModifiers.Alt, 0x22u)]
    [InlineData("Ctrl++", HotkeyModifiers.Control, 0xBBu)]
    [InlineData("F13", HotkeyModifiers.None, 0x7Cu)]
    public void Parses_valid_gestures(string text, HotkeyModifiers modifiers, uint virtualKey)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out string? error), error);
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(virtualKey, gesture.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("A")]
    public void Rejects_invalid_gestures_with_a_message(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("shift+ctrl+space", "Ctrl+Shift+Space")]
    [InlineData("Alt+Ctrl+Shift+s", "Ctrl+Alt+Shift+S")]
    [InlineData("Ctrl+Return", "Ctrl+Enter")]
    [InlineData("Ctrl+OemMinus", "Ctrl+Minus")]
    [InlineData("Ctrl+D7", "Ctrl+7")]
    public void ToString_produces_the_canonical_form(string input, string canonical) =>
        Assert.Equal(canonical, HotkeyGesture.Parse(input).ToString());

    [Fact]
    public void Canonical_text_round_trips()
    {
        var original = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Win, 0x4B); // K
        Assert.Equal(original, HotkeyGesture.Parse(original.ToString()));
    }

    [Theory]
    [InlineData(0x10u, true)]
    [InlineData(0xA2u, true)]
    [InlineData(0x5Bu, true)]
    [InlineData(0x41u, false)]
    public void Recognizes_modifier_virtual_keys(uint vk, bool expected) =>
        Assert.Equal(expected, HotkeyGesture.IsModifierKey(vk));
}
