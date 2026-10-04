using Lumen.Core.Llm.Local;

namespace Lumen.Core.Tests;

public class StopSequenceDetectorTests
{
    [Fact]
    public void Plain_text_passes_through_unchanged()
    {
        var detector = new StopSequenceDetector(["<|end|>"]);

        Assert.Equal("Hello", detector.Push("Hello"));
        Assert.Equal(" world", detector.Push(" world"));
        Assert.Equal("", detector.Flush());
        Assert.False(detector.Stopped);
    }

    [Fact]
    public void A_marker_split_across_pieces_stops_generation_and_is_never_shown()
    {
        var detector = new StopSequenceDetector(["<|end|>"]);

        string shown = detector.Push("Done.<|") + detector.Push("en") + detector.Push("d|>more");

        Assert.Equal("Done.", shown);
        Assert.True(detector.Stopped);
        Assert.Equal("", detector.Push("ignored"));
    }

    [Fact]
    public void A_held_back_prefix_is_released_when_it_turns_out_to_be_ordinary_text()
    {
        var detector = new StopSequenceDetector(["<|end|>"]);

        Assert.Equal("a ", detector.Push("a <|"));
        Assert.Equal("<|x", detector.Push("x"));
        Assert.False(detector.Stopped);
    }

    [Fact]
    public void Flush_returns_text_that_was_held_back_at_the_very_end()
    {
        var detector = new StopSequenceDetector(["<|end|>"]);

        Assert.Equal("x", detector.Push("x<|e"));
        Assert.Equal("<|e", detector.Flush());
    }

    [Fact]
    public void The_earliest_of_several_markers_wins()
    {
        var detector = new StopSequenceDetector(["<|end|>", "<|user|>"]);

        Assert.Equal("answer", detector.Push("answer<|user|>question<|end|>"));
        Assert.True(detector.Stopped);
    }
}
