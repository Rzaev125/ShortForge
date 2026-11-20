using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public sealed class ExportRenderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("Экспорт O'Brien ").FullName;

    [RealMediaFact]
    public async Task VerifiedBundleContainsRequestedOutputsAndUsesOriginal()
    {
        var ffmpeg = LibraryTests.Ffmpeg!;
        var ffprobe = LibraryTests.Ffprobe!;
        string source = Path.Combine(root, "Оригинал '1'.mp4");
        await MediaProcess.RunAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=192x108:r=30",
            "-f", "lavfi", "-i", "sine=f=500", "-af", "volume=0.1", "-t", "2", "-c:v", "libx264", "-c:a", "aac", source],
            TimeSpan.FromSeconds(20), 0, default);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var lib = new Library(root);
        var project = await lib.ImportAsync(source, ffprobe, null, default);
        var cached = await lib.CreateProxyAsync(project.Id, ffprobe, ffmpeg, default);
        Assert.True(File.Exists(cached.ProxyPath));
        // A corrupt playback cache must have no effect on an export from the original.
        await File.WriteAllBytesAsync(cached.ProxyPath!, [1, 2, 3]);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(root, "library.sqlite3"), Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO transcript_cache(project_id,cache_key,selected,result_json) VALUES($id,'fixture',1,$transcript)";
            cmd.Parameters.AddWithValue("$id", project.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$transcript", JsonSerializer.Serialize(new Transcript(0, "ru", "ru", "test", 1, 0,
                [new(400_000, 850_000, "Фраза")])));
            cmd.ExecuteNonQuery();
        }
        var clip = lib.CreateEdit(project.Id);
        clip = lib.SaveEdit(project.Id, clip.Id, clip.Revision, clip.Edit with {StartUs = 150_000, EndUs = 1_550_000}, null);
        var job = Guid.NewGuid();
        var path = Library.ExportDestination(root, "CON:/?*'тест' " + new string('я', 240), job);
        var progress = new RecordingProgress();
        await lib.RenderExportJobAsync(project.Id, clip.Id, clip.Revision, job, path, new(60, true, true, true),
            ffmpeg, ffprobe, progress, default);
        Assert.Contains(progress.Values, value => value > 0);
        var probe = await MediaProbe.ReadAsync(ffprobe, path, default);
        Assert.Equal(("60/1", "h264", "aac", 1080, 1920), (probe.Fps, probe.VideoCodec, probe.FirstAudioCodec, probe.Width, probe.Height));
        Assert.InRange(probe.DurationUs, 1_390_000, 1_420_000);
        Assert.Contains("00:00:00,250 --> 00:00:00,700", await File.ReadAllTextAsync(Path.ChangeExtension(path, ".srt")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(path, ".json")));
        Assert.Equal(hash, manifest.RootElement.GetProperty("originalSha256").GetString());
        Assert.Equal("libx264", manifest.RootElement.GetProperty("encoder").GetString());
        Assert.Equal("Фраза", clip.Cues.Single().Text);
        string pcm = Path.Combine(root, "нормализованный.pcm");
        await MediaProcess.RunAsync(ffmpeg, ["-v","error","-i",path,"-vn","-ac","1","-ar","8000","-f","s16le",pcm],
            TimeSpan.FromSeconds(20),0,default);
        var samples = await File.ReadAllBytesAsync(pcm);
        Assert.Contains(Enumerable.Range(0, samples.Length / 2), i => BitConverter.ToInt16(samples, i * 2) > 2000);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<UserError>(() => lib.RenderExportJobAsync(project.Id, clip.Id, clip.Revision,
                job, path, new(), ffmpeg, ffprobe, progress, default));
            Assert.Equal("export_exists", error.Code);
        }
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
        Assert.Empty(Directory.EnumerateDirectories(root, ".shortforge-*"));

        var hardwareJob = Guid.NewGuid();
        var hardwarePath = Library.ExportDestination(root, "Ускорение", hardwareJob);
        await lib.RenderExportJobAsync(project.Id, clip.Id, clip.Revision, hardwareJob, hardwarePath,
            new(30, false, false, true, true), ffmpeg, ffprobe, progress, default);
        using var hardwareManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(hardwarePath, ".json")));
        Assert.Contains(hardwareManifest.RootElement.GetProperty("encoder").GetString(), new[] {"h264_nvenc", "libx264"});
        Assert.False(File.Exists(Path.ChangeExtension(hardwarePath, ".srt")));

        var failedJob = Guid.NewGuid();
        var failedPath = Library.ExportDestination(root, "Ошибка", failedJob);
        // ffprobe reads metadata but cannot encode. Its real non-zero exit must never publish a partial bundle.
        var failed = await Assert.ThrowsAsync<UserError>(() => lib.RenderExportJobAsync(project.Id, clip.Id,
            clip.Revision, failedJob, failedPath, new(), ffprobe, ffprobe, progress, default));
        Assert.Equal("caption_render", failed.Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(failedPath)));
        Assert.Empty(Directory.EnumerateDirectories(root, ".shortforge-*"));
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }

    [RealMediaFact]
    public async Task QueueFitsRotatedSilentVfrAndKeepsPresentationTime()
    {
        var ffmpeg = LibraryTests.Ffmpeg!;
        var ffprobe = LibraryTests.Ffprobe!;
        string unrotated = Path.Combine(root, "временные метки.mp4");
        string source = Path.Combine(root, "поворот 90.mp4");
        Task<string> Ffmpeg(params string[] args) => MediaProcess.RunAsync(ffmpeg, ["-v", "error", "-nostdin", .. args],
            TimeSpan.FromSeconds(30), 0, default);
        await Ffmpeg("-f", "lavfi", "-i", "color=red:s=160x80:r=30:d=2", "-vf",
            "drawbox=x=80:y=0:w=80:h=80:color=blue:t=fill,select='not(eq(mod(n,3),1))'",
            "-fps_mode", "vfr", "-an", "-c:v", "libx264", unrotated);
        await Ffmpeg("-display_rotation:v:0", "90", "-i", unrotated, "-c", "copy", source);
        var db = new Library(root);
        var project = await db.ImportAsync(source, ffprobe, null, default);
        var sample = (await db.MeasureFrameIntervalsAsync(project.Id, ffprobe, default)).FrameIntervals!;
        Assert.True(sample.MaxMs > sample.MinMs * 1.5);
        var clip = db.CreateEdit(project.Id);
        db.SaveEdit(project.Id, clip.Id, clip.Revision, clip.Edit with
            { StartUs = 115_000, EndUs = 1_315_000, Frame = "fit" }, null);
        var job = db.EnqueueExports(project.Id, [clip.Id], root, new(IncludeSrt: true)).Single();
        await db.RunExportQueueAsync(() => (ffmpeg, ffprobe), default);
        Assert.Equal("completed", db.ListExportJobs().Single().State);
        string mp4 = Path.Combine(db.ExportFolder(job.Id), job.FileName);
        Assert.False(File.Exists(Path.ChangeExtension(mp4, ".srt")));
        var info = await MediaProbe.ReadAsync(ffprobe, mp4, default);
        Assert.Equal((0, 0d, "30/1", 1_200_000L), (info.AudioTracks, info.Rotation, info.Fps, info.DurationUs));
        string frame = Path.Combine(root, "кадр.rgb");
        await Ffmpeg("-i", mp4, "-frames:v", "1", "-vf", "scale=90:160", "-pix_fmt", "rgb24", "-f", "rawvideo", frame);
        byte[] rgb = await File.ReadAllBytesAsync(frame);
        int Pixel(int x, int y, int channel) => rgb[(y * 90 + x) * 3 + channel];
        Assert.True(Pixel(45, 40, 2) > 200 && Pixel(45, 120, 0) > 200);
        Assert.True(Pixel(1, 40, 2) < 16 && Pixel(88, 120, 0) < 16);
    }

    [RealMediaFact]
    public async Task WindowsFolderDenyingSubdirectoryCreationLeavesNoPublishedOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ffmpeg = LibraryTests.Ffmpeg!;
        var ffprobe = LibraryTests.Ffprobe!;
        string source = Path.Combine(root, "локальное видео.mp4");
        await MediaProcess.RunAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=s=128x96",
            "-t", "0.5", "-c:v", "libx264", source], TimeSpan.FromSeconds(20), 0, default);
        var library = new Library(root);
        var project = await library.ImportAsync(source, ffprobe, null, default);
        var clip = library.CreateEdit(project.Id);
        var folder = Directory.CreateDirectory(Path.Combine(root, "Закрытая папка"));
        var originalAcl = folder.GetAccessControl(AccessControlSections.Access);
        var blockedAcl = folder.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        blockedAcl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateDirectories, AccessControlType.Deny));
        try
        {
            folder.SetAccessControl(blockedAcl);
            var job = Guid.NewGuid();
            string path = Library.ExportDestination(folder.FullName, "Клип", job);
            var error = await Assert.ThrowsAsync<UserError>(() => library.RenderExportJobAsync(project.Id, clip.Id,
                clip.Revision, job, path, new(), ffmpeg, ffprobe, new RecordingProgress(), default));
            Assert.Equal("export_access", error.Code);
            Assert.Empty(folder.EnumerateFileSystemInfos());
        }
        finally { folder.SetAccessControl(originalAcl); }
    }

    public void Dispose() => Directory.Delete(root, true);
}
