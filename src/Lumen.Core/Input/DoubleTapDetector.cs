namespace Lumen.Core.Input;

/// <summary>
/// Recognizes "tap Ctrl twice quickly" from a raw stream of key events.
/// </summary>
/// <remarks>
/// A "tap" is Ctrl pressed and released quickly with no other key in between, so ordinary
/// shortcuts (Ctrl+C, Ctrl+V pressed twice in a row...) never count. Keeping this as pure logic,
/// fed with virtual-key codes and timestamps, makes it testable without a real keyboard hook.
/// </remarks>
public sealed class DoubleTapDetector
{
    private const uint VkControl = 0x11;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;

    private readonly uint _maxTapMs;
    private readonly uint _maxGapMs;

    private bool _ctrlDown;
    private uint _ctrlDownAt;
    private bool _otherKeyDuringTap;
    private bool _haveFirstTap;
    private uint _firstTapUpAt;

    /// <param name="maxTapMs">Longest a single tap may hold Ctrl down.</param>
    /// <param name="maxGapMs">Longest pause between the first release and the second release.</param>
    public DoubleTapDetector(uint maxTapMs = 300, uint maxGapMs = 450)
    {
        _maxTapMs = maxTapMs;
        _maxGapMs = maxGapMs;
    }

    /// <summary>Feeds one key event.</summary>
    /// <param name="virtualKey">The Win32 virtual-key code.</param>
    /// <param name="isDown">True for key down, false for key up.</param>
    /// <param name="timeMs">Event time in milliseconds (the hook's monotonic tick count).</param>
    /// <returns>True exactly when this event completes a double tap.</returns>
    public bool OnKey(uint virtualKey, bool isDown, uint timeMs)
    {
        bool isCtrl = virtualKey is VkControl or VkLeftControl or VkRightControl;

        if (!isCtrl)
        {
            if (isDown)
            {
                // Any other key cancels both the tap in progress and a pending first tap.
                _otherKeyDuringTap = true;
                _haveFirstTap = false;
            }

            return false;
        }

        if (isDown)
        {
            if (!_ctrlDown) // ignore auto-repeat while Ctrl is held
            {
                _ctrlDown = true;
                _ctrlDownAt = timeMs;
                _otherKeyDuringTap = false;
            }

            return false;
        }

        // Ctrl released.
        _ctrlDown = false;
        bool wasTap = !_otherKeyDuringTap && unchecked(timeMs - _ctrlDownAt) <= _maxTapMs;
        if (!wasTap)
        {
            _haveFirstTap = false;
            return false;
        }

        if (_haveFirstTap && unchecked(timeMs - _firstTapUpAt) <= _maxGapMs)
        {
            _haveFirstTap = false;
            return true;
        }

        _haveFirstTap = true;
        _firstTapUpAt = timeMs;
        return false;
    }
}
