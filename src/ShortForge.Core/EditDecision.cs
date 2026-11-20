using System.Text.Json.Serialization;

namespace ShortForge.Core;

public sealed record CaptionStyle(string Preset, int Size, string Color, int Outline, bool Background, int Position)
{
    public static CaptionStyle Neutral { get; } = new("neutral", 64, "#FFFFFF", 3, false, 85);

    [JsonIgnore]
    public bool IsValid => Preset is "neutral" or "contrast" && Size is >= 32 and <= 112 &&
        Color is { Length: 7 } && Color[0] == '#' && Color.Skip(1).All(char.IsAsciiHexDigit) &&
        Outline is >= 0 and <= 10 && Position is >= 10 and <= 94;
}

public sealed record EditDecision(string Title, long StartUs, long EndUs, string Frame, double X, double Y,
    CaptionStyle? CaptionStyle = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CropTrajectory? Trajectory = null)
{
    [JsonIgnore]
    public CaptionStyle Style => CaptionStyle ?? ShortForge.Core.CaptionStyle.Neutral;

    public bool Fits(long durationUs) => !string.IsNullOrWhiteSpace(Title) && Title.Length <= 240 &&
        StartUs >= 0 && StartUs < EndUs && EndUs <= durationUs && Frame is "crop" or "fit" or "auto" &&
        X is >= 0 and <= 1 && Y is >= 0 and <= 1 && Style.IsValid;

    public (long StartUs, long EndUs)? ClipTime(long sourceStart, long sourceEnd)
    {
        long start = Math.Max(StartUs, sourceStart), end = Math.Min(EndUs, sourceEnd);
        return start < end ? (start - StartUs, end - StartUs) : null;
    }
}

public sealed record CueChange(int Index, string Text, long? SourceStartUs = null, long? SourceEndUs = null);
