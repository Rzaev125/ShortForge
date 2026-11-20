using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

internal static class FaceAnalysis
{
    internal static async Task<IReadOnlyList<FaceSample>> ReadAsync(string source, MediaProbe input,
        long startUs, long endUs, int intervalMs, string ffmpeg, IProgress<ImportProgress>? progress,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (intervalMs is < 100 or > 2000 || startUs < 0 || endUs <= startUs || endUs > input.DurationUs)
            throw new UserError("face_interval", "Проверьте границы клипа и интервал анализа (100–2000 мс).");
        long step = intervalMs * 1000L;
        long count = 1 + (endUs - startUs - 1) / step;
        if (count > CropTrajectory.MaxPoints)
            throw new UserError("face_limit", "Для клипа требуется больше 3600 кадров анализа. Увеличьте интервал или сократите клип.");
        var helper = Path.Combine(AppContext.BaseDirectory, "ShortForge.FaceDetector.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(helper))
            throw new UserError("face_missing", "Не найден локальный детектор лиц. Восстановите ShortForge.FaceDetector.exe из сборки Windows.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            using var detector = Start(helper, [], true);
            using var detectorJob = WindowsChildJob.Attach(detector);
            using var stopDetector = deadline.Token.Register(() => Stop(detector));
            if (await detector.StandardOutput.ReadLineAsync(deadline.Token) != "READY")
                throw DetectorUnavailable();

            string seconds(long us) => (us / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture);
            // FFmpeg autorotates first. Scale converts anamorphic frames to square pixels.
            // The fps grid is relative to the input seek; round=up selects the last frame
            // presented by each sample time. A frame limit stops decode precisely after the
            // final required sample; an output -t rounds to the nearest frame and can drop it.
            string filter = FormattableString.Invariant($"fps=1000/{intervalMs}:start_time=0:round=up:eof_action=pass,") +
                "scale=w='min(640,iw)':h='min(640,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,format=gray";
            using var decoder = Start(ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-threads", "2", "-protocol_whitelist", "file",
                 "-format_whitelist", MediaProbe.StandaloneFormats, "-ss", seconds(startUs), "-noaccurate_seek",
                 "-i", source, "-map", FormattableString.Invariant($"0:{input.StreamIndex}"), "-an", "-sn", "-dn",
                 "-filter_threads", "1", "-vf", filter, "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                 "-threads", "1", "-c:v", "pgm", "-f", "image2pipe", "pipe:1"], false);
            using var decoderJob = WindowsChildJob.Attach(decoder);
            using var stopDecoder = deadline.Token.Register(() => Stop(decoder));
            var transfer = PipeFramesAsync(decoder, detector, deadline.Token);
            var diagnostics = decoder.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var samples = new List<FaceSample>((int)count);
            try
            {
                while (await detector.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    if (samples.Count >= count) throw InvalidFrames();
                    samples.Add(Parse(line, startUs + step * samples.Count));
                    progress?.Report(new("faceAnalysis", (int)(samples.Count * 100L / count)));
                }
                await detector.WaitForExitAsync(deadline.Token);
                if (detector.ExitCode is 2 or 3) throw DetectorUnavailable();
                if (detector.ExitCode != 0) throw InvalidFrames();
                await decoder.WaitForExitAsync(deadline.Token);
                if (decoder.ExitCode != 0 || samples.Count != count) throw InvalidFrames();
                token.ThrowIfCancellationRequested();
                return samples;
            }
            finally
            {
                Stop(decoder);
                Stop(detector);
                // Closing the owned processes releases a blocked pipe on both sides.
                try { await Task.WhenAll(transfer, diagnostics); }
                catch (IOException) when (decoder.HasExited || detector.HasExited) { }
            }
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw new UserError("face_timeout", "Анализ лиц превысил 20 минут. Увеличьте интервал или сократите клип.");
        }
    }

    private static async Task PipeFramesAsync(Process decoder, Process detector, CancellationToken token)
    {
        try
        {
            using var frames = decoder.StandardOutput.BaseStream;
            using var input = detector.StandardInput;
            await frames.CopyToAsync(input.BaseStream, 64 * 1024, token);
        }
        catch (IOException)
        {
            Stop(decoder);
            Stop(detector);
            throw;
        }
    }

    private static Process Start(string executable, string[] arguments, bool detector)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = detector,
            RedirectStandardOutput = true, RedirectStandardError = !detector
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try { return Process.Start(info) ?? throw new Win32Exception(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            if (detector) throw DetectorUnavailable();
            throw new UserError("tool_unavailable", "Не удалось запустить FFmpeg для чтения кадров. Проверьте путь в настройках.");
        }
    }

    private static FaceSample Parse(string line, long timeUs)
    {
        var values = line.Split(' ');
        if (values.Length < 3 || (values.Length - 3) % 4 != 0) throw InvalidFrames();
        int value(int i) => int.TryParse(values[i], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number : throw InvalidFrames();
        int width = value(0), height = value(1), cut = value(2);
        if (width is < 1 or > 640 || height is < 1 or > 640 || cut > 1) throw InvalidFrames();
        var faces = new FaceBox[(values.Length - 3) / 4];
        for (int i = 0; i < faces.Length; i++)
        {
            int offset = 3 + i * 4;
            int x = value(offset), y = value(offset + 1), w = value(offset + 2), h = value(offset + 3);
            if (w < 1 || h < 1 || x > width - w || y > height - h) throw InvalidFrames();
            faces[i] = new((double)x / width, (double)y / height, (double)w / width, (double)h / height);
        }
        return new(timeUs, cut == 1, faces);
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) when (process.HasExited) { }
    }

    private static UserError InvalidFrames() =>
        new("face_frames", "Не удалось прочитать все кадры клипа. Проверьте исходник, границы клипа и выбранный FFmpeg.");

    private static UserError DetectorUnavailable() =>
        new("face_unavailable", "Детектор лиц Windows недоступен для этих кадров. Проверьте компонент Windows.Media.FaceAnalysis или выберите ручное кадрирование.");
}
