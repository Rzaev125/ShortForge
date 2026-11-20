using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public sealed class CaptionRenderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("Заголовки O'Brien ").FullName;
    private static string Ffmpeg => LibraryTests.Ffmpeg!;
    private static string Ffprobe => LibraryTests.Ffprobe!;
    private static EditorClip Example(params EditorCue[] cues) =>
        new(Guid.NewGuid(), 2, 0, new("Тест", 5_000_000, 9_000_000, "crop", .5, .5), cues);

    [Fact]
    public void AssAndSrtClipAndRebaseWithoutLosingLiteralText()
    {
        var clip = Example(new(0, 100_000, 1_234_000, @"{\pos(1,1)} Привет \N", 5_100_000, 6_234_000),
            new(1, 1_600_000, 2_820_000, "<b>əı şğ öü 😀</b>", 6_600_000, 7_820_000));
        var ass = CaptionFiles.ToAss(clip, 800_000, 1_000_000);
        Assert.Contains("0:00:00.00,0:00:00.43", ass);
        Assert.Contains("0:00:00.80,0:00:01.00", ass);
        Assert.DoesNotContain(@"{\pos(1,1)}", ass);
        Assert.Contains("<b>əı şğ öü 😀</b>", ass);
        var srt = CaptionFiles.ToSrt(clip);
        Assert.Contains("00:00:00,100 --> 00:00:01,234", srt);
        Assert.Contains("00:00:01,600 --> 00:00:02,820", srt);
        Assert.Contains("&lt;b&gt;əı şğ öü 😀&lt;/b&gt;", srt);
    }

    [Fact]
    public void WideUnbrokenWordWrapsAndUnreadablyTallTextReportsThePhrase()
    {
        string longWord = string.Concat(Enumerable.Repeat("Şğİə", 15));
        Assert.Contains(@"\N", CaptionFiles.ToAss(Example(new EditorCue(0, 0, 3_000_000, longWord, 5_000_000, 8_000_000)), 0, 3_000_000));
        var oversized = Example(new EditorCue(0, 0, 3_000_000, new string('Ж', 8000), 5_000_000, 8_000_000));
        Assert.Equal("caption_overflow", Assert.Throws<UserError>(() => CaptionFiles.ToAss(oversized, 0, 3_000_000)).Code);
        Assert.Contains(new string('Ж', 8000), CaptionFiles.ToSrt(oversized));
    }

    [RealMediaFact]
    public async Task RenderUsesCurrentTextClipTimeAndVerifiedOriginal()
    {
        var source = Path.Combine(root, "Проверка 'титров' " + new string('я', 68) + ".mp4");
        await Run(["-f", "lavfi", "-i", "color=black:s=160x90:r=30", "-f", "lavfi", "-i", "anullsrc",
            "-f", "lavfi", "-i", "sine=frequency=440", "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-t", "2", "-c:v", "libx264", "-c:a", "aac", source]);
        var digest = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var library = new Library(root);
        var project = await library.ImportAsync(source, Ffprobe, null, default);
        var transcript = new Transcript(1, "ru", "ru", "test", 1, 1, [new(500_000, 1_200_000, "До")]);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(root, "library.sqlite3"), Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO transcript_cache(project_id,cache_key,selected,result_json) VALUES($id,'fixture',1,$transcript)";
            cmd.Parameters.AddWithValue("$id", project.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$transcript", JsonSerializer.Serialize(transcript));
            cmd.ExecuteNonQuery();
        }
        var clip = library.CreateEdit(project.Id);
        clip = library.SaveEdit(project.Id, clip.Id, clip.Revision, clip.Edit with { StartUs = 200_000, EndUs = 1_750_000 }, null);
        Task<string> Render(int revision, CancellationToken token = default) =>
            library.RenderCaptionPreviewAsync(project.Id, clip.Id, revision, 200_000, Ffmpeg, Ffprobe, token);
        var first = await Render(clip.Revision);
        var probe = await MediaProbe.ReadAsync(Ffprobe, first, default);
        Assert.Equal((1080, 1920, "h264", "aac"), (probe.Width, probe.Height, probe.VideoCodec, probe.FirstAudioCodec));
        Assert.InRange(probe.DurationUs, 1_325_000, 1_380_000);
        var pcm = Path.Combine(root, "decoded.pcm");
        await Run(["-i", first, "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", pcm]);
        var samples = await File.ReadAllBytesAsync(pcm);
        Assert.Contains(Enumerable.Range(0, samples.Length / 2), i => BitConverter.ToInt16(samples, 2 * i) > 1000);
        Assert.Equal(await CaptionMask(first, "0.03"), await CaptionMask(first, "1.00"));
        Assert.NotEqual(await CaptionMask(first, "0.03"), await CaptionMask(first, "0.30"));
        var updated = library.SaveEdit(project.Id, clip.Id, clip.Revision, clip.Edit,
            new(0, @"Привет {\pos(1,1)} \N <i>əı şğ öü 😀</i>"));
        Assert.Equal("edit_changed", (await Assert.ThrowsAsync<UserError>(() => Render(clip.Revision))).Code);
        var second = await Render(updated.Revision);
        Assert.NotEqual(await Frame(first, "0.30"), await Frame(second, "0.30"));
        var full = Path.Combine(root, "Готовый фильм '2026'.mp4");
        await library.RenderCaptionClipAsync(project.Id, clip.Id, updated.Revision, full, Ffmpeg, Ffprobe, default);
        var fullProbe = await MediaProbe.ReadAsync(Ffprobe, full, default);
        Assert.InRange(fullProbe.DurationUs, 1_525_000, 1_580_000);
        // A constant source isolates the subtitle pixels. Compare the same clip time at 0.20+0.30 s.
        var previewPixels = await CaptionPixels(second, "0.30");
        var fullPixels = await CaptionPixels(full, "0.50");
        int intersection = 0, union = 0;
        for (int i = 0; i < previewPixels.Length; i++)
        {
            if (previewPixels[i] > 0 && fullPixels[i] > 0) intersection++;
            if (previewPixels[i] > 0 || fullPixels[i] > 0) union++;
        }
        Assert.True(union > 1000 && intersection >= .99 * union,
            $"Пересечение масок субтитров {intersection} из {union} пикселей");
        var tail = await library.RenderCaptionPreviewAsync(project.Id, clip.Id, updated.Revision,
            1_549_000, Ffmpeg, Ffprobe, default);
        Assert.InRange((await MediaProbe.ReadAsync(Ffprobe, tail, default)).DurationUs, 1000, 34_000);
        await Assert.ThrowsAsync<UserError>(() =>
            library.RenderCaptionClipAsync(project.Id, clip.Id, updated.Revision, source, Ffmpeg, Ffprobe, default));
        Assert.Equal(digest, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Render(updated.Revision, new CancellationToken(true)));
        var modified = await File.ReadAllBytesAsync(source);
        modified[^1] ^= 1;
        await File.WriteAllBytesAsync(source, modified);
        Assert.Equal("source_mismatch", (await Assert.ThrowsAsync<UserError>(() => Render(updated.Revision))).Code);
    }

    [RealMediaFact]
    public async Task RotatedAnamorphicOriginalUsesDisplayAspectAndLastPresentedFrame()
    {
        var baseVideo = Path.Combine(root, "анаморф.mp4");
        var source = Path.Combine(root, "с поворотом.mp4");
        await Run(["-f", "lavfi", "-i", "color=red:s=120x80:r=2,drawbox=x=60:y=0:w=60:h=80:c=blue:t=fill,setsar=2",
            "-t", "2", "-an", "-c:v", "libx264", baseVideo]);
        await Run(["-display_rotation:v:0", "90", "-i", baseVideo, "-c", "copy", source]);
        var library = new Library(root);
        var project = await library.ImportAsync(source, Ffprobe, null, default);
        var clip = library.CreateEdit(project.Id);
        async Task<byte[]> Render(string mode, double y, long offset)
        {
            clip = library.SaveEdit(project.Id, clip.Id, clip.Revision, clip.Edit with { Frame = mode, Y = y }, null);
            var mp4 = await library.RenderCaptionPreviewAsync(project.Id, clip.Id, clip.Revision, offset, Ffmpeg, Ffprobe, default);
            var rgb = Path.Combine(root, Guid.NewGuid().ToString("N") + ".rgb");
            await Run(["-i", mp4, "-frames:v", "1", "-vf", "scale=54:96", "-pix_fmt", "rgb24", "-f", "rawvideo", rgb]);
            return await File.ReadAllBytesAsync(rgb);
        }
        static byte[] Pixel(byte[] pixels, int x, int y) => pixels.AsSpan(3 * (54 * y + x), 3).ToArray();
        var fit = await Render("fit", .5, 1_999_000);
        Assert.All(Pixel(fit, 5, 48), value => Assert.InRange((int)value, 0, 5));
        Assert.True(Pixel(fit, 27, 20)[2] > 200);
        Assert.True(Pixel(fit, 27, 75)[0] > 200);
        var top = await Render("crop", 0, 1_900_000);
        var bottom = await Render("crop", 1, 1_900_000);
        Assert.True(Pixel(top, 5, 48)[2] > 200);
        Assert.True(Pixel(bottom, 5, 48)[0] > 200);
    }

    [RealMediaFact]
    public async Task LibassFindsSystemFallbackForUnavailableFont()
    {
        var clip = Example(new EditorCue(0, 0, 500_000, "Привет · Əə ı ş ğ ö ü", 5_000_000, 5_500_000));
        var ass = CaptionFiles.ToAss(clip, 0, 500_000).Replace("Segoe UI", "ShortForge Deliberately Missing Typeface");
        await File.WriteAllTextAsync(Path.Combine(root, "fallback.ass"), ass);
        async Task<string> Mask(string vf) => await MediaProcess.RunAsync(Ffmpeg, ["-v", "error", "-f", "lavfi",
            "-i", "color=black:s=1080x1920", "-vf", vf, "-frames:v", "1", "-f", "md5", "-"],
            TimeSpan.FromSeconds(15), 128, default, workingDirectory: root);
        Assert.NotEqual(await Mask("null"), await Mask("ass=fallback.ass"));
    }

    [RealMediaFact]
    public async Task ActiveCancellationAndConcurrentEditDiscardWorkWithoutPublishing()
    {
        var input = Path.Combine(root, "8 секунд.mp4");
        await Run(["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=15", "-t", "8", "-an", "-c:v", "libx264", input]);
        var library = new Library(root);
        var project = await library.ImportAsync(input, Ffprobe, null, default);
        var clip = library.CreateEdit(project.Id);
        var destination = Path.Combine(root, "финал.mp4");
        async Task WaitForEncoder(Task job)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!Directory.EnumerateFiles(root, "render.mp4", SearchOption.AllDirectories).Any())
            {
                if (job.IsCompleted) await job;
                await Task.Delay(10, deadline.Token);
            }
        }
        using var cancel = new CancellationTokenSource();
        var cancelled = library.RenderCaptionClipAsync(project.Id, clip.Id, clip.Revision, destination, Ffmpeg, Ffprobe, cancel.Token);
        await WaitForEncoder(cancelled);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(root, ".shortforge-*"));

        var stale = library.RenderCaptionClipAsync(project.Id, clip.Id, clip.Revision, destination, Ffmpeg, Ffprobe, default);
        await WaitForEncoder(stale);
        var saved = library.SaveEdit(project.Id, clip.Id, clip.Revision, clip.Edit with { Frame = "fit" }, null);
        Assert.Equal("edit_changed", (await Assert.ThrowsAsync<UserError>(() => stale)).Code);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(root, ".shortforge-*"));
        var preview = await library.RenderCaptionPreviewAsync(project.Id, clip.Id, saved.Revision, 1_000_000, Ffmpeg, Ffprobe, default);
        Assert.Equal(5_000_000, (await MediaProbe.ReadAsync(Ffprobe, preview, default)).DurationUs);
    }

    private static Task<string> Frame(string path, string time) =>
        Run(["-ss", time, "-i", path, "-map", "0:v:0", "-frames:v", "1", "-f", "md5", "-"]);

    private static Task<string> CaptionMask(string path, string time) =>
        Run(["-ss", time, "-i", path, "-map", "0:v:0", "-frames:v", "1", "-vf",
            "format=gray,lut=y='if(gte(val,128),255,0)'", "-f", "md5", "-"]);

    private async Task<byte[]> CaptionPixels(string path, string time)
    {
        var target = Path.Combine(root, Guid.NewGuid().ToString("N") + ".gray");
        await Run(["-ss", time, "-i", path, "-map", "0:v:0", "-frames:v", "1", "-vf",
            "format=gray,lut=y='if(gte(val,128),255,0)'", "-f", "rawvideo", target]);
        var bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal(1080 * 1920, bytes.Length);
        return bytes;
    }

    private static Task<string> Run(string[] args) => MediaProcess.RunAsync(Ffmpeg, ["-v", "error", "-nostdin", .. args],
        TimeSpan.FromSeconds(45), 65536, default);

    public void Dispose() => Directory.Delete(root, recursive: true);
}
