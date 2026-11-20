using System.Globalization;
using System.Text;
using System.Text.Json;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record ExportSettings(int Fps = 30, bool NormalizeAudio = true, bool IncludeSrt = false,
    bool IncludeManifest = false, bool PreferNvenc = false);

public sealed partial class Library
{
    public static string ExportDestination(string root, string title, Guid namingId)
    {
        if (!IsLocalExportPath(root) || namingId == Guid.Empty)
            throw InvalidExportPath();
        var invalid = Path.GetInvalidFileNameChars();
        // The fixed prefix also handles Windows device names (CON, NUL, COM1, etc.).
        var stem = new StringBuilder("Клип ");
        foreach (var rune in title.EnumerateRunes())
        {
            if (stem.Length + rune.Utf16SequenceLength > 64) break;
            if (rune.IsBmp && Array.IndexOf(invalid, (char)rune.Value) >= 0) stem.Append('_');
            else stem.Append(rune.ToString());
        }
        var name = stem.ToString().TrimEnd(' ', '.');
        return Path.Combine(Path.GetFullPath(root), $"{name}-{namingId:N}", name + ".mp4");
    }

    private static bool IsLocalExportPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var root = Path.GetPathRoot(path)!;
        if (OperatingSystem.IsWindows() && !(root.Length == 3 && char.IsAsciiLetter(root[0]) && root[1] == ':'))
            return false;
        return !path[root.Length..].Contains(':');
    }

    private static UserError InvalidExportPath() => new("export_destination", "Выберите существующую локальную папку для экспорта.");
    private static UserError OccupiedExport() => new("export_exists", "Имя результата занято. Повторите экспорт для сохранения под новым именем.");

    internal async Task RenderExportJobAsync(Guid projectId, Guid clipId, int revision, Guid jobId,
        string destinationMp4, ExportSettings settings, string ffmpeg, string ffprobe, IProgress<int> progress,
        CancellationToken token)
    {
        if (settings.Fps is not (30 or 60)) throw new UserError("export_fps", "Выберите 30 или 60 кадров в секунду.");
        var (root, final, stage) = ExportDirectories(destinationMp4, jobId);
        await captionRenderer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(final) || Directory.Exists(final)) throw OccupiedExport();
            if (!Directory.Exists(root)) throw InvalidExportPath();
            if (Directory.Exists(stage) || File.Exists(stage))
                throw new UserError("export_work_exists", "Рабочая папка задания занята. Проверьте папку назначения.");
            var clip = OpenEdit(projectId, clipId);
            if (clip.Revision != revision) throw ChangedEdit();
            var length = clip.Edit.EndUs - clip.Edit.StartUs;
            var original = ReadOriginal(projectId);
            using var source = await OpenMatchingOriginalAsync(original.Path, original.Size, original.Hash, token);
            var input = await MediaProbe.ReadAsync(ffprobe, source.Name, token);
            var ass = CaptionFiles.ToAss(clip, 0, length);
            Directory.CreateDirectory(stage);
            var marker = Path.Combine(stage, "owner");
            using (var claim = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                claim.Write(jobId.ToByteArray());
            try
            {
                var bundle = Directory.CreateDirectory(Path.Combine(stage, "result")).FullName;
                var temporary = Path.Combine(bundle, Path.GetFileName(destinationMp4));
                var partial = Path.Combine(stage, "render.partial");
                await File.WriteAllTextAsync(Path.Combine(stage, "captions.ass"), ass, Encoding.UTF8, token);
                bool nvenc = settings.PreferNvenc && await ProbeNvencAsync(ffmpeg, settings.Fps, token);
                int reported = 0;
                void ReportTime(long us)
                {
                    var percent = Math.Clamp((int)(100d * us / length), 0, 99);
                    if (percent <= reported) return;
                    reported = percent;
                    progress.Report(percent);
                }
                Task Encode(bool hardware) => EncodeCaptionWindowAsync(clip, 0, length, input, source.Name, ffmpeg,
                    stage, partial, token, settings.Fps, settings.NormalizeAudio, hardware, ReportTime);
                try { await Encode(nvenc); }
                catch (UserError e) when (nvenc && e.Code == "caption_render")
                {
                    File.Delete(partial);
                    nvenc = false;
                    await Encode(false);
                }
                var output = await MediaProbe.ReadAsync(ffprobe, partial, token);
                VerifyCaptionRender(output, length, input, settings.Fps);
                File.Move(partial, temporary);
                bool writeSrt = settings.IncludeSrt && clip.Cues.Length > 0;
                if (writeSrt)
                    await File.WriteAllTextAsync(Path.ChangeExtension(temporary, ".srt"), CaptionFiles.ToSrt(clip), token);
                if (settings.IncludeManifest)
                {
                    var manifest = new { schemaVersion = 1, jobId, projectId, clipId, revision,
                        title = clip.Edit.Title, originalSha256 = original.Hash, originalWidth = input.Width,
                        originalHeight = input.Height, sourceStartUs = clip.Edit.StartUs, sourceEndUs = clip.Edit.EndUs,
                        durationUs = output.DurationUs, frame = clip.Edit.Frame, x = clip.Edit.X, y = clip.Edit.Y,
                        trajectory = clip.Edit.Frame == "auto" ? clip.Edit.Trajectory : null,
                        fps = settings.Fps, encoder = nvenc ? "h264_nvenc" : "libx264", video = Path.GetFileName(temporary),
                        audioTrack = input.AudioTracks > 0 ? clip.AudioTrack : (int?)null,
                        normalizedAudio = settings.NormalizeAudio && input.AudioTracks > 0,
                        subtitles = writeSrt ? Path.GetFileName(Path.ChangeExtension(temporary, ".srt")) : null };
                    await File.WriteAllTextAsync(Path.ChangeExtension(temporary, ".json"),
                        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
                }
                await PrepareExportCompletionAsync(jobId, temporary, token);
                PublishCurrentRevision(projectId, clipId, revision, token, () => Directory.Move(bundle, final), jobId);
            }
            finally { CleanInterruptedExport(destinationMp4, jobId); }
        }
        catch (UnauthorizedAccessException) { throw new UserError("export_access", "Нет права записи в папку экспорта. Выберите другую папку."); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 39 or 112)
        { throw new UserError("export_disk_full", "На диске назначения недостаточно свободного места."); }
        catch (IOException) when (File.Exists(final) || Directory.Exists(final)) { throw OccupiedExport(); }
        catch (IOException) { throw new UserError("export_io", "Не удалось записать экспорт. Проверьте доступ, свободное место и занятые файлы."); }
        finally { captionRenderer.Release(); }
    }

    private static async Task<bool> ProbeNvencAsync(string ffmpeg, int fps, CancellationToken token)
    {
        try
        {
            await MediaProcess.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i",
                "color=black:s=1080x1920:r=" + fps.ToString(CultureInfo.InvariantCulture), "-frames:v", "2", "-an",
                .. VideoEncoderArguments(true), "-pix_fmt", "yuv420p", "-f", "null", "-"], TimeSpan.FromSeconds(20), 0, token);
            return true;
        }
        catch (UserError e) when (e.Code is "media_unreadable" or "tool_timeout") { return false; }
    }

    private static (string Root, string Final, string Stage) ExportDirectories(string destinationMp4, Guid id)
    {
        if (id == Guid.Empty || !IsLocalExportPath(destinationMp4) ||
            !Path.GetExtension(destinationMp4).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw InvalidExportPath();
        var final = Path.GetDirectoryName(Path.GetFullPath(destinationMp4))!;
        var root = Path.GetDirectoryName(final) ?? throw InvalidExportPath();
        var prefix = Path.GetFileNameWithoutExtension(destinationMp4) + "-";
        var folderName = Path.GetFileName(final);
        if (!folderName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(folderName[prefix.Length..], "N", out var attemptId)) throw InvalidExportPath();
        return (root, final, Path.Combine(root, $".shortforge-{attemptId:N}"));
    }

    internal static void CleanInterruptedExport(string destinationMp4, Guid jobId)
    {
        try
        {
            var (_, _, stage) = ExportDirectories(destinationMp4, jobId);
            if (!Directory.Exists(stage) || IsReparsePoint(stage)) return;
            var marker = Path.Combine(stage, "owner");
            if (!File.Exists(marker) || IsReparsePoint(marker)) return;
            using (var claim = File.OpenRead(marker))
            {
                Span<byte> bytes = stackalloc byte[16];
                if (claim.Length != bytes.Length) return;
                claim.ReadExactly(bytes);
                if (new Guid(bytes) != jobId) return;
            }
            // A crash may leave part of the bundle. Remove only our own known items.
            var bundle = Path.Combine(stage, "result");
            if (Directory.Exists(bundle))
            {
                if (IsReparsePoint(bundle)) return;
                foreach (var extension in new[] { ".mp4", ".srt", ".json" })
                    File.Delete(Path.Combine(bundle, Path.ChangeExtension(Path.GetFileName(destinationMp4), extension)));
                Directory.Delete(bundle);
            }
            File.Delete(Path.Combine(stage, "captions.ass"));
            File.Delete(Path.Combine(stage, "framing.ffscript"));
            File.Delete(Path.Combine(stage, "render.partial"));
            File.Delete(marker);
            Directory.Delete(stage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserError or ArgumentException) { }
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
