using System.Runtime.InteropServices;
using System.Windows;
using Lumen.App.Interop;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>
/// Copies the current selection of another application and pastes text back into it,
/// leaving the user's clipboard exactly as it was.
/// </summary>
/// <remarks>
/// All methods must be called on the WPF UI thread: the clipboard is an OLE/COM API and needs
/// a single-threaded apartment (STA), which is what WPF's UI thread is. The waits are
/// <c>await Task.Delay</c>, never Thread.Sleep, so the UI thread keeps pumping messages
/// while the other application processes the keystrokes we sent it.
/// </remarks>
public sealed class ClipboardService
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN

    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(ILogger<ClipboardService> logger) => _logger = logger;

    /// <summary>Sends Ctrl+C to the foreground window and returns what it copied (null if nothing).</summary>
    public async Task<string?> CopySelectionAsync(CancellationToken cancellationToken = default)
    {
        await KeyboardInput.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(1200), cancellationToken);

        DataObject? saved = await SnapshotAsync();
        uint before = NativeMethods.GetClipboardSequenceNumber();

        KeyboardInput.SendChord(NativeMethods.VK_CONTROL, NativeMethods.VK_C);

        // Wait for the target application to put something on the clipboard.
        bool changed = false;
        for (int waited = 0; waited < 600; waited += 15)
        {
            await Task.Delay(15, cancellationToken);
            if (NativeMethods.GetClipboardSequenceNumber() != before)
            {
                changed = true;
                break;
            }
        }

        string? text = null;
        if (changed)
        {
            // Some apps write several formats one after the other; give them a moment to finish.
            await Task.Delay(40, cancellationToken);
            text = await RetryAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        }
        else
        {
            _logger.LogInformation("Ctrl+C did not change the clipboard (nothing selected, or the app ignores Ctrl+C)");
        }

        if (changed)
        {
            await RestoreAsync(saved);
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Puts <paramref name="text"/> into <paramref name="targetWindow"/> as if the user pasted it.</summary>
    public async Task<bool> PasteIntoAsync(nint targetWindow, string text, CancellationToken cancellationToken = default)
    {
        if (targetWindow == 0 || !NativeMethods.IsWindow(targetWindow))
        {
            return false;
        }

        DataObject? saved = await SnapshotAsync();
        if (!await RetryAsync(() => { Clipboard.SetDataObject(text, copy: true); return true; }))
        {
            return false;
        }

        NativeMethods.SetForegroundWindow(targetWindow);
        await Task.Delay(120, cancellationToken); // let the window become active before typing into it
        await KeyboardInput.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(800), cancellationToken);
        KeyboardInput.SendChord(NativeMethods.VK_CONTROL, NativeMethods.VK_V);

        // The target reads the clipboard asynchronously after receiving Ctrl+V; restoring the old
        // content too early would paste the wrong thing.
        await Task.Delay(500, cancellationToken);
        await RestoreAsync(saved);
        return true;
    }

    public static Task<bool> SetTextAsync(string text) =>
        RetryAsync(() => { Clipboard.SetDataObject(text, copy: true); return true; });

    /// <summary>Copies every format currently on the clipboard into a detached DataObject.</summary>
    private async Task<DataObject?> SnapshotAsync()
    {
        return await RetryAsync(() =>
        {
            IDataObject? current = Clipboard.GetDataObject();
            if (current is null)
            {
                return null;
            }

            var copy = new DataObject();
            foreach (string format in current.GetFormats(autoConvert: false))
            {
                try
                {
                    // Delay-rendered or private formats can fail to materialize; skip those.
                    object? data = current.GetData(format, autoConvert: false);
                    if (data is not null)
                    {
                        copy.SetData(format, data, autoConvert: false);
                    }
                }
                catch (Exception ex) when (ex is COMException or OutOfMemoryException or ExternalException or InvalidOperationException)
                {
                    _logger.LogDebug("Clipboard format {Format} could not be saved: {Message}", format, ex.Message);
                }
            }

            return copy.GetFormats().Length > 0 ? copy : null;
        });
    }

    private async Task RestoreAsync(DataObject? saved)
    {
        bool ok = await RetryAsync(() =>
        {
            if (saved is null)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetDataObject(saved, copy: true);
            }

            return true;
        });

        if (!ok)
        {
            _logger.LogWarning("Could not restore the previous clipboard content");
        }
    }

    /// <summary>
    /// The clipboard is a global resource: if another process has it open, every call throws
    /// CLIPBRD_E_CANT_OPEN. That state lasts milliseconds, so a short retry loop solves it.
    /// </summary>
    private static async Task<T?> RetryAsync<T>(Func<T?> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException ex) when (ex.HResult == ClipboardBusyHResult && attempt < 10)
            {
                await Task.Delay(25 + attempt * 15);
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                return default;
            }
        }
    }
}

/// <summary>Synthesizes key presses with SendInput.</summary>
internal static class KeyboardInput
{
    private static readonly ushort[] Modifiers =
        [NativeMethods.VK_SHIFT, NativeMethods.VK_CONTROL, NativeMethods.VK_MENU, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN];

    /// <summary>
    /// The hotkey fires while the user is still holding its modifiers. If we sent Ctrl+C now,
    /// a held Shift would turn it into Ctrl+Shift+C. So wait (briefly) for a clean keyboard,
    /// and if the user keeps holding a key, release it logically ourselves.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (AnyModifierDown() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15, cancellationToken);
        }

        foreach (ushort vk in Modifiers.Where(IsDown))
        {
            Send(vk, keyUp: true);
        }
    }

    /// <summary>Presses modifier+key and releases both, in one atomic SendInput call.</summary>
    public static unsafe void SendChord(ushort modifier, ushort key)
    {
        NativeMethods.INPUT* inputs = stackalloc NativeMethods.INPUT[4];
        inputs[0] = Key(modifier, keyUp: false);
        inputs[1] = Key(key, keyUp: false);
        inputs[2] = Key(key, keyUp: true);
        inputs[3] = Key(modifier, keyUp: true);
        NativeMethods.SendInput(4, inputs, sizeof(NativeMethods.INPUT));
    }

    private static unsafe void Send(ushort vk, bool keyUp)
    {
        NativeMethods.INPUT input = Key(vk, keyUp);
        NativeMethods.SendInput(1, &input, sizeof(NativeMethods.INPUT));
    }

    private static NativeMethods.INPUT Key(ushort vk, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0 },
        },
    };

    private static bool AnyModifierDown() => Modifiers.Any(IsDown);

    // The high bit of GetAsyncKeyState is set while the key is physically down.
    private static bool IsDown(ushort vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
}
