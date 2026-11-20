namespace ShortForge.Core;

public sealed record ClipSearch(string Mode, int MinSeconds, int MaxSeconds, int Count)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Mode is "structure" or "semantic" && MinSeconds >= 1 && MinSeconds <= MaxSeconds &&
        MaxSeconds <= 180 && Count is >= 1 and <= 10;
    public bool Fits(long durationUs) => durationUs >= MinSeconds * 1_000_000L && durationUs <= MaxSeconds * 1_000_000L;
}
