using System.Globalization;
using System.Text;
using System.Text.Json;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed partial class Library
{
    private readonly SemaphoreSlim captionRenderer = new(1);

    public Task<string> RenderCaptionPreviewAsync(Guid projectId, Guid clipId, int revision,
        long offsetUs, string ffmpeg, string ffprobe, CancellationToken token) =>
        RenderCaptionAsync(projectId, clipId, revision, offsetUs, null, ffmpeg, ffprobe, token);

    public Task RenderCaptionClipAsync(Guid projectId, Guid clipId, int revision, string destination,
        string ffmpeg, string ffprobe, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(destination) || new Uri(destination).IsUnc ||
            !Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(destination).Contains(':'))
            throw new UserError("caption_destination", "Выберите новый локальный файл MP4.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new UserError("caption_exists", "Файл с таким именем уже существует. Выберите новое имя для клипа.");
        return RenderCaptionAsync(projectId, clipId, revision, 0, destination, ffmpeg, ffprobe, token);
    }

    private async Task<string> RenderCaptionAsync(Guid projectId, Guid clipId, int revision, long offsetUs,
        string? destination, string ffmpeg, string ffprobe, CancellationToken token)
    {
        await captionRenderer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var clip = OpenEdit(projectId, clipId);
            if (clip.Revision != revision) throw ChangedEdit();
            long length = clip.Edit.EndUs - clip.Edit.StartUs;
            if (offsetUs < 0 || offsetUs >= length)
                throw new UserError("caption_interval", "Поместите курсор перед концом клипа.");
            long durationUs = destination == null ? Math.Min(5_000_000, length - offsetUs) : length;
            var script = CaptionFiles.ToAss(clip, offsetUs, durationUs);
            using var source = await OpenOriginalForProcessingAsync(projectId, token).ConfigureAwait(false);
            var input = await MediaProbe.ReadAsync(ffprobe, source.Name, token).ConfigureAwait(false);
            var directory = destination == null
                ? Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(proxyDirectory)!, "caption-previews"))
                : new DirectoryInfo(Path.GetDirectoryName(destination)!);
            string id = Guid.NewGuid().ToString("N");
            var work = directory.CreateSubdirectory(".shortforge-" + id);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(work.FullName, "captions.ass"), script, Encoding.UTF8, token);
                string temporary = Path.Combine(work.FullName, "render.mp4");
                await EncodeCaptionWindowAsync(clip, offsetUs, durationUs, input, source.Name, ffmpeg, work.FullName, temporary, token);
                var output = await MediaProbe.ReadAsync(ffprobe, temporary, token);
                VerifyCaptionRender(output, durationUs, input);
                var final = destination ?? Path.Combine(directory.FullName, id + ".mp4");
                PromoteCaptionFile(projectId, clipId, revision, temporary, final, false, token);
                return final;
            }
            finally { work.Delete(recursive: true); }
        }
        finally { captionRenderer.Release(); }
    }

    // Both preview and full-length composition use the original's autorotated frame and display aspect.
    // A fixed relative ASS filename avoids all filtergraph path quoting (including Windows drive letters).
    internal static async Task EncodeCaptionWindowAsync(EditorClip clip, long offsetUs, long durationUs,
        MediaProbe input, string source, string ffmpeg, string workDirectory, string output, CancellationToken token,
        int fps = 30, bool normalizeAudio = false, bool nvenc = false, Action<long>? encodedTime = null)
    {
        if (input.IsHdr && input.Color?.CanToneMap != true)
            throw new UserError("caption_hdr", "Для предпросмотра в SDR нужны PQ/HLG с BT.2020 NC и известным диапазоном.");
        if (input.AudioTracks > 0 && (clip.AudioTrack < 0 || clip.AudioTrack >= input.AudioTracks))
            throw new UserError("audio_track", "Дорожка сохранённого клипа недоступна.");
        var edit = clip.Edit;
        string frame = FramingFilter.Build(edit, offsetUs, durationUs);
        if (input.IsHdr)
        {
            string peak = input.Color!.Transfer == "smpte2084" ? "100" : "10";
            string toneMap = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=709," +
                $"tonemap=hable:peak={peak}:desat=0,zscale=t=709:m=709:r=limited:dither=error_diffusion,format=yuv420p,sidedata=mode=delete";
            frame = edit.Frame == "auto" ? toneMap + "," + frame : frame + "," + toneMap;
        }
        // Keep decoder preroll so fps can select the picture already on screen at In,
        // including the final held frame of a low-frame-rate or VFR recording.
        string video = FormattableString.Invariant($"fps={fps}:start_time=0:round=up,") + frame + ",ass=filename=captions.ass";
        var args = new List<string> { "-nostdin", "-v", "error", "-n", "-xerror", "-protocol_whitelist", "file",
            "-format_whitelist", MediaProbe.StandaloneFormats, "-threads", "2", "-ss", Seconds(edit.StartUs + offsetUs),
            "-noaccurate_seek", "-i", source, "-t", Seconds(durationUs), "-map", FormattableString.Invariant($"0:{input.StreamIndex}") };
        if (encodedTime != null) args.AddRange(["-progress", "pipe:1", "-nostats"]);
        if (input.AudioTracks > 0)
        {
            var audio = $"atrim=start=0:end={Seconds(durationUs)},aresample=48000:async=1:first_pts=0";
            if (normalizeAudio) audio += ",loudnorm=I=-16:LRA=11:TP=-1.5,aresample=48000";
            args.AddRange(["-map", FormattableString.Invariant($"0:a:{clip.AudioTrack}"), "-af",
                audio + ",apad", "-c:a", "aac", "-b:a", "128k", "-ac", "2"]);
        }
        // Windows limits the whole command line to 32K. A generated trajectory can exceed it.
        if (edit.Frame == "auto")
        {
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "framing.ffscript"), video, token);
            args.AddRange(["-/filter:v", "framing.ffscript"]);
        }
        else args.AddRange(["-vf", video]);
        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-filter_threads", "2",
            .. VideoEncoderArguments(nvenc), "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-f", "mp4", output]);
        try
        {
            await MediaProcess.RunAsync(ffmpeg, args, Timeout.InfiniteTimeSpan, 0, token, encodedTime,
                workingDirectory: workDirectory, checkOutputErrors: true);
        }
        catch (UserError e) when (e.Code == "media_unreadable")
        {
            throw new UserError("caption_render", "FFmpeg не создал видео с субтитрами. Проверьте свободное место и поддержку libass, libx264 и AAC.");
        }
    }

    private static string Seconds(long us) => (us / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture);

    private static string[] VideoEncoderArguments(bool nvenc) => nvenc
        ? ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0"]
        : ["-c:v", "libx264", "-threads:v", "2", "-preset", "veryfast", "-crf", "20"];

    private static void VerifyCaptionRender(MediaProbe output, long duration, MediaProbe input, int fps = 30)
    {
        using var document = JsonDocument.Parse(output.Json);
        var stream = MediaProbe.SelectVideo(document.RootElement.GetProperty("streams").EnumerateArray());
        bool HasExpectedDuration(JsonElement s) => s.TryGetProperty("duration", out var dt) &&
            decimal.TryParse(dt.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0 && Math.Abs(seconds * 1_000_000 - duration) <= 50_000;
        var streams = document.RootElement.GetProperty("streams").EnumerateArray();
        if (streams.Count() != 1 + Math.Min(input.AudioTracks, 1) ||
            streams.Any(s => !HasExpectedDuration(s)) || Math.Abs(output.DurationUs - duration) > 50_000 ||
            output.VideoCodec != "h264" || !output.Container.Split(',').Contains("mp4") ||
            output.Width != 1080 || output.Height != 1920 || output.Fps != FormattableString.Invariant($"{fps}/1") ||
            !stream.TryGetProperty("sample_aspect_ratio", out var sar) || sar.GetString() != "1:1" ||
            !stream.TryGetProperty("pix_fmt", out var format) || format.GetString() != "yuv420p" ||
            output.Rotation != 0 || output.AudioTracks != Math.Min(input.AudioTracks, 1) ||
            (output.AudioTracks == 1 && output.FirstAudioCodec != "aac") ||
            (input.IsHdr && !output.IsLimitedBt709Yuv420))
            throw new UserError("caption_render_invalid", "Параметры проверенного видео не совпадают с кадром, интервалом или звуком клипа.");
    }
}
