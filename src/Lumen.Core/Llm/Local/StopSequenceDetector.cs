using System.Text;

namespace Lumen.Core.Llm.Local;

/// <summary>
/// Watches streamed text for stop markers that may be split across several pieces.
/// </summary>
/// <remarks>
/// Example with the marker "&lt;|end|&gt;": the model may emit "Done.&lt;|", then "end", then "|&gt;".
/// "Done." can be shown immediately, but "&lt;|" must be held back until we know whether it
/// grows into the marker (then we stop and drop it) or into ordinary text (then we release it).
/// Only the longest suffix that is still a prefix of some marker is ever held back.
/// </remarks>
public sealed class StopSequenceDetector
{
    private readonly string[] _markers;
    private readonly StringBuilder _pending = new();

    public StopSequenceDetector(IEnumerable<string> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        _markers = markers.Where(m => m.Length > 0).ToArray();
    }

    public bool Stopped { get; private set; }

    /// <summary>Adds a newly generated piece.</summary>
    /// <returns>The text that is now safe to show (possibly empty).</returns>
    public string Push(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        if (Stopped)
        {
            return "";
        }

        _pending.Append(piece);
        string text = _pending.ToString();

        // 1. A complete marker anywhere in the pending text: emit what precedes it and stop.
        int stopAt = -1;
        foreach (string marker in _markers)
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (stopAt < 0 || index < stopAt))
            {
                stopAt = index;
            }
        }

        if (stopAt >= 0)
        {
            Stopped = true;
            _pending.Clear();
            return text[..stopAt];
        }

        // 2. Hold back the longest tail that could still become a marker.
        int hold = LongestMarkerPrefixSuffix(text);
        string safe = text[..^hold];
        _pending.Clear().Append(text, text.Length - hold, hold);
        return safe;
    }

    /// <summary>Generation ended: whatever is still held back was ordinary text after all.</summary>
    public string Flush()
    {
        if (Stopped)
        {
            return "";
        }

        string rest = _pending.ToString();
        _pending.Clear();
        return rest;
    }

    private int LongestMarkerPrefixSuffix(string text)
    {
        int best = 0;
        foreach (string marker in _markers)
        {
            int max = Math.Min(marker.Length - 1, text.Length);
            for (int len = max; len > best; len--)
            {
                if (text.AsSpan(text.Length - len).SequenceEqual(marker.AsSpan(0, len)))
                {
                    best = len;
                    break;
                }
            }
        }

        return best;
    }
}
