using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lumen.Core.Input;

/// <summary>
/// Modifier keys of a global hotkey. The numeric values are exactly Win32's MOD_ALT,
/// MOD_CONTROL, MOD_SHIFT and MOD_WIN, so they can be passed to RegisterHotKey unchanged.
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1714:Flags enums should have plural names", Justification = "Mirrors the Win32 naming.")]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A key combination such as "Ctrl+Shift+Space": modifiers plus one Win32 virtual-key code.
/// </summary>
/// <remarks>
/// Lives in Core (not in the WPF project) so it can be unit-tested and so the settings file
/// stores a readable string instead of numbers.
/// </remarks>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NamedKeys = BuildNamedKeys();
    private static readonly Dictionary<uint, string> KeyNames = BuildKeyNames();

    public static bool TryParse(string? text, out HotkeyGesture gesture, [NotNullWhen(false)] out string? error)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The shortcut is empty.";
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        uint? key = null;

        // "Ctrl++" means Ctrl and the plus key; spell the key out before splitting on '+'.
        string normalized = text.Trim();
        if (normalized.EndsWith("++", StringComparison.Ordinal))
        {
            normalized = normalized[..^1] + "Plus";
        }

        foreach (string part in normalized.Split('+', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0)
            {
                error = $"'{text}' has an empty part.";
                return false;
            }

            HotkeyModifiers modifier = ParseModifier(part);
            if (modifier != HotkeyModifiers.None)
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                error = $"'{text}' contains more than one non-modifier key.";
                return false;
            }

            if (!TryParseKey(part, out uint vk))
            {
                error = $"'{part}' is not a key Lumen knows.";
                return false;
            }

            key = vk;
        }

        if (key is null)
        {
            error = "A shortcut needs a key in addition to the modifiers (e.g. Ctrl+Shift+Space).";
            return false;
        }

        bool isFunctionKey = key >= 0x70 && key <= 0x87; // F1..F24
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            error = "Use at least one modifier (Ctrl, Alt, Shift or Win), otherwise the key stops working in every other app.";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.Value);
        error = null;
        return true;
    }

    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out HotkeyGesture gesture, out string? error) ? gesture : throw new FormatException(error);

    /// <summary>Canonical text, always in the order Ctrl, Alt, Shift, Win, key.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    public static string KeyName(uint virtualKey) =>
        KeyNames.TryGetValue(virtualKey, out string? name) ? name : "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>True for the virtual keys of Ctrl/Alt/Shift/Win themselves (a gesture cannot end with them).</summary>
    public static bool IsModifierKey(uint virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    private static HotkeyModifiers ParseModifier(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" or "META" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static bool TryParseKey(string part, out uint vk)
    {
        if (NamedKeys.TryGetValue(part, out vk))
        {
            return true;
        }

        if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(part.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk) && vk is > 0 and < 0xFF)
        {
            return true;
        }

        vk = 0;
        return false;
    }

    private static Dictionary<string, uint> BuildNamedKeys()
    {
        var keys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            keys[c.ToString()] = c; // VK_A..VK_Z equal the ASCII codes
        }

        for (char c = '0'; c <= '9'; c++)
        {
            keys[c.ToString()] = c; // VK_0..VK_9 too
            keys["D" + c] = c;      // WPF's Key enum spells them D0..D9
            keys["NumPad" + c] = 0x60u + (uint)(c - '0');
        }

        for (uint f = 1; f <= 24; f++)
        {
            keys["F" + f.ToString(CultureInfo.InvariantCulture)] = 0x6Fu + f;
        }

        (string Name, uint Vk)[] named =
        [
            ("Space", 0x20), ("Enter", 0x0D), ("Return", 0x0D), ("Tab", 0x09), ("Escape", 0x1B), ("Esc", 0x1B),
            ("Backspace", 0x08), ("Back", 0x08), ("Insert", 0x2D), ("Delete", 0x2E), ("Del", 0x2E),
            ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("Prior", 0x21), ("PageDown", 0x22), ("Next", 0x22),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("PrintScreen", 0x2C), ("Snapshot", 0x2C), ("Pause", 0x13),
            ("Multiply", 0x6A), ("Add", 0x6B), ("Subtract", 0x6D), ("Decimal", 0x6E), ("Divide", 0x6F),
            ("Semicolon", 0xBA), ("OemSemicolon", 0xBA), ("Plus", 0xBB), ("OemPlus", 0xBB), ("Comma", 0xBC), ("OemComma", 0xBC),
            ("Minus", 0xBD), ("OemMinus", 0xBD), ("Period", 0xBE), ("OemPeriod", 0xBE), ("Slash", 0xBF), ("OemQuestion", 0xBF),
            ("Backquote", 0xC0), ("OemTilde", 0xC0), ("OpenBracket", 0xDB), ("OemOpenBrackets", 0xDB),
            ("Backslash", 0xDC), ("OemPipe", 0xDC), ("CloseBracket", 0xDD), ("OemCloseBrackets", 0xDD),
            ("Quote", 0xDE), ("OemQuotes", 0xDE),
        ];

        foreach ((string name, uint vk) in named)
        {
            keys[name] = vk;
        }

        return keys;
    }

    private static Dictionary<uint, string> BuildKeyNames()
    {
        // The first (canonical) name wins when several names map to the same key.
        var names = new Dictionary<uint, string>();
        string[] preferred =
        [
            "Space", "Enter", "Tab", "Escape", "Backspace", "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
            "Left", "Up", "Right", "Down", "PrintScreen", "Pause", "Multiply", "Add", "Subtract", "Decimal", "Divide",
            "Semicolon", "Plus", "Comma", "Minus", "Period", "Slash", "Backquote", "OpenBracket", "Backslash", "CloseBracket", "Quote",
        ];

        foreach (string name in preferred)
        {
            names.TryAdd(NamedKeys[name], name);
        }

        foreach ((string name, uint vk) in NamedKeys)
        {
            if (!name.StartsWith('D') || name.Length != 2)
            {
                names.TryAdd(vk, name);
            }
        }

        return names;
    }
}
