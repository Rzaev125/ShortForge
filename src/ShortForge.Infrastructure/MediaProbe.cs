using System.Globalization;
using System.Text.Json;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record VideoColor(bool IsHdr, string? Transfer, string? Primaries, bool CanToneMap);
public sealed record AnamorphicAspect(string PixelRatio, string? FrameRatio);

internal sealed record MediaProbe(string Json, long DurationUs, int StreamIndex, int Width, int Height,
    string VideoCodec, string Container, int AudioTracks, string? Fps, double Rotation, VideoColor? Color,
    bool IsLimitedBt709Yuv420, string? FirstAudioCodec)
{
    internal bool IsHdr => Color?.IsHdr == true;
    // Standalone containers only. Playlist/concat/image-sequence demuxers can open other
    // files named by the input, even when network protocols have been disabled.
    internal const string StandaloneFormats =
        "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi,flv,mpeg,mpegts,asf,ogg,mxf,nut,rm";

    internal static async Task<MediaProbe> ReadAsync(string probe, string path, CancellationToken token)
    {
        var json = await MediaProcess.RunAsync(probe,
            ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", StandaloneFormats,
             "-show_streams", "-show_format", "-of", "json", "-i", path],
            TimeSpan.FromSeconds(45), 4 * 1024 * 1024, token);
        try
        {
            using var document = JsonDocument.Parse(json);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = SelectVideo(streams);
            if (video.ValueKind == JsonValueKind.Undefined)
                throw new UserError("no_video", "В файле нет видеодорожки.");
            var format = document.RootElement.GetProperty("format");
            decimal duration = Seconds(Text(format, "duration")) ?? Seconds(Text(video, "duration")) ?? 0;
            if (duration <= 0 || duration > long.MaxValue / 1_000_000m)
                throw new UserError("duration_unknown", "Не удалось определить длительность видео.");
            int width = video.GetProperty("width").GetInt32(), height = video.GetProperty("height").GetInt32();
            if (width <= 0 || height <= 0) throw InvalidMetadata();
            string? fps = Text(video, "avg_frame_rate");
            if (fps is null or "0/0" or "0/1") fps = null;
            double rotation = 0;
            if (video.TryGetProperty("tags", out var tags))
                double.TryParse(Text(tags, "rotate"), CultureInfo.InvariantCulture, out rotation);
            if (video.TryGetProperty("side_data_list", out var sides))
                foreach (var side in sides.EnumerateArray())
                    if (side.TryGetProperty("rotation", out var value) && value.TryGetDouble(out var angle))
                    { rotation = angle; break; }
            var audio = streams.Where(s => Text(s, "codec_type") == "audio").ToArray();
            return new MediaProbe(json, checked((long)decimal.Round(duration * 1_000_000m)),
                video.GetProperty("index").GetInt32(), width, height, Text(video, "codec_name") ?? "unknown",
                Text(format, "format_name") ?? "unknown", audio.Length,
                fps, double.IsFinite(rotation) ? rotation : 0, ReadColor(video),
                Text(video, "color_space") == "bt709" && Text(video, "color_transfer") == "bt709" &&
                Text(video, "color_primaries") == "bt709" && Text(video, "color_range") == "tv" &&
                Text(video, "pix_fmt") == "yuv420p",
                audio.Length > 0 ? Text(audio[0], "codec_name") : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                                   or FormatException or OverflowException)
        { throw InvalidMetadata(); }
    }

    internal static JsonElement SelectVideo(IEnumerable<JsonElement> streams) =>
        streams.Where(s => Text(s, "codec_type") == "video" && Flag(s, "attached_pic") == 0)
            .OrderByDescending(s => Flag(s, "default")).FirstOrDefault();

    internal static VideoColor? ReadColor(JsonElement video)
    {
        if (video.ValueKind != JsonValueKind.Object) return null;
        var transfer = Text(video, "color_transfer");
        var primaries = Text(video, "color_primaries");
        bool hdr = transfer is "smpte2084" or "arib-std-b67" ||
            (video.TryGetProperty("side_data_list", out var sides) && sides.EnumerateArray()
                .Any(side => Text(side, "side_data_type") is "DOVI configuration record" or
                    "Mastering display metadata" or "Content light level metadata"));
        bool canToneMap = transfer is ("smpte2084" or "arib-std-b67") && primaries == "bt2020" &&
            Text(video, "color_space") == "bt2020nc" && Text(video, "color_range") is ("tv" or "pc") &&
            !(video.TryGetProperty("side_data_list", out var sideData) &&
                sideData.EnumerateArray().Any(s => Text(s, "side_data_type") == "DOVI configuration record"));
        return transfer == null && primaries == null && !hdr ? null : new(hdr, transfer, primaries, canToneMap);
    }

    internal static AnamorphicAspect? ReadAnamorphicAspect(JsonElement video)
    {
        if (video.ValueKind != JsonValueKind.Object) return null;
        var sar = Text(video, "sample_aspect_ratio");
        var dar = Text(video, "display_aspect_ratio");
        if (!PositiveRatio(sar, out var w, out var h) || w == h) return null;
        return new(sar!, PositiveRatio(dar, out _, out _) ? dar : null);
    }

    private static bool PositiveRatio(string? text, out int w, out int h)
    {
        w = h = 0;
        return text?.Split(':') is [var numerator, var denominator] &&
            int.TryParse(numerator, NumberStyles.None, CultureInfo.InvariantCulture, out w) && w > 0 &&
            int.TryParse(denominator, NumberStyles.None, CultureInfo.InvariantCulture, out h) && h > 0;
    }

    private static string? Text(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Flag(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty(name, out var flag)
            && flag.TryGetInt32(out int value) ? value : 0;

    private static decimal? Seconds(string? value) => decimal.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null;

    private static UserError InvalidMetadata() => new("invalid_probe", "ffprobe вернул неполные или повреждённые метаданные видео.");
}
