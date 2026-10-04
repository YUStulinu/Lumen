using Lumen.Core.Input;

namespace Lumen.Core.Tests;

public class DoubleTapDetectorTests
{
    private const uint LeftCtrl = 0xA2;
    private const uint KeyC = 0x43;

    [Fact]
    public void Two_quick_taps_trigger_on_the_second_release()
    {
        var d = new DoubleTapDetector();

        Assert.False(d.OnKey(LeftCtrl, true, 1000));
        Assert.False(d.OnKey(LeftCtrl, false, 1080));
        Assert.False(d.OnKey(LeftCtrl, true, 1200));
        Assert.True(d.OnKey(LeftCtrl, false, 1270));
    }

    [Fact]
    public void Auto_repeat_key_downs_do_not_restart_the_tap()
    {
        var d = new DoubleTapDetector();

        d.OnKey(LeftCtrl, true, 0);
        d.OnKey(LeftCtrl, true, 30); // auto-repeat
        d.OnKey(LeftCtrl, false, 90);
        d.OnKey(LeftCtrl, true, 200);
        Assert.True(d.OnKey(LeftCtrl, false, 260));
    }

    [Fact]
    public void Ctrl_C_pressed_twice_is_not_a_double_tap()
    {
        var d = new DoubleTapDetector();

        for (uint t = 0; t < 2; t++)
        {
            uint start = t * 200;
            d.OnKey(LeftCtrl, true, start);
            d.OnKey(KeyC, true, start + 20);
            d.OnKey(KeyC, false, start + 40);
            Assert.False(d.OnKey(LeftCtrl, false, start + 60));
        }
    }

    [Fact]
    public void Too_long_a_pause_between_taps_does_not_trigger()
    {
        var d = new DoubleTapDetector(maxTapMs: 300, maxGapMs: 450);

        d.OnKey(LeftCtrl, true, 0);
        d.OnKey(LeftCtrl, false, 50);
        d.OnKey(LeftCtrl, true, 900);
        Assert.False(d.OnKey(LeftCtrl, false, 950));
    }

    [Fact]
    public void Holding_ctrl_is_not_a_tap()
    {
        var d = new DoubleTapDetector(maxTapMs: 300);

        d.OnKey(LeftCtrl, true, 0);
        d.OnKey(LeftCtrl, false, 50);
        d.OnKey(LeftCtrl, true, 100);
        Assert.False(d.OnKey(LeftCtrl, false, 800));
    }

    [Fact]
    public void A_triple_tap_triggers_only_once()
    {
        var d = new DoubleTapDetector();
        int triggers = 0;

        for (uint i = 0; i < 3; i++)
        {
            d.OnKey(LeftCtrl, true, i * 150);
            if (d.OnKey(LeftCtrl, false, (i * 150) + 50))
            {
                triggers++;
            }
        }

        Assert.Equal(1, triggers);
    }
}
