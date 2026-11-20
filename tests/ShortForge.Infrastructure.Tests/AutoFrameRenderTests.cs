using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public sealed class AutoFrameRenderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("Кадр O'Brien ").FullName;
    private static string Ffmpeg => LibraryTests.Ffmpeg!;
    private static string Ffprobe => LibraryTests.Ffprobe!;

    [RealMediaFact]
    public async Task MaximumTrajectoryIsAcceptedByWindowsFfmpeg()
    {
        const long step = 100_000;
        var points = Enumerable.Range(0, CropTrajectory.MaxPoints)
            .Select(i => new CropPoint(i * step, "crop", .5 + .45 * Math.Sin(i * .12), .5, i == 0)).ToArray();
        var path = new CropTrajectory(100, points, new(points.Length, 0, 0, 0));
        var edit = new EditDecision("Полная траектория", 0, step * points.Length, "auto", .5, .5, Trajectory:path);
        string script = FramingFilter.Build(edit, 0, edit.EndUs);
        Assert.True(script.Length > 32_767);
        string file = Path.Combine(root, "кадрирование.ffscript");
        await File.WriteAllTextAsync(file, script);
        await Run("-f", "lavfi", "-i", "color=s=160x90:r=30:d=0.1", "-/filter:v", file,
            "-frames:v", "1", "-f", "null", "-");
    }

    [RealMediaFact]
    public async Task OffsetPreviewAndFullExportFollowStoredMotionAndFitWithoutSubtitles()
    {
        var source = Path.Combine(root, "Полосы слева направо.mp4");
        await Run("-f", "lavfi", "-i", "color=red:s=192x108:r=30:d=2",
            "-vf", "drawbox=x=64:y=0:w=64:h=ih:c=lime:t=fill,drawbox=x=128:y=0:w=64:h=ih:c=blue:t=fill",
            "-c:v", "libx264", source);
        var lib = new Library(root);
        var projectId = (await lib.ImportAsync(source, Ffprobe, null, default)).Id;
        var clip = lib.CreateEdit(projectId);
        var path = new CropTrajectory(200, [new(0,"crop",0,.5,true), new(800_000,"crop",1,.5,false),
            new(1_000_000,"fit",.5,.5,true), new(1_400_000,"crop",1,.5,true)], new(3,0,1,2));
        // Seed a known trajectory to isolate the renderer from the detector's classification.
        var edit = clip.Edit with { StartUs = 200_000, Frame = "auto", Trajectory = path };
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "library.sqlite3"), Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE edit_clip SET decision=$decision WHERE id=$id";
            cmd.Parameters.AddWithValue("$decision", JsonSerializer.Serialize(edit));
            cmd.Parameters.AddWithValue("$id", clip.Id.ToString("D"));
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var preview = await lib.RenderCaptionPreviewAsync(projectId, clip.Id, clip.Revision, 300_000, Ffmpeg, Ffprobe, default);
        var job = Guid.NewGuid();
        var full = Library.ExportDestination(root, "Движение", job);
        await lib.RenderExportJobAsync(projectId, clip.Id, clip.Revision, job, full, new(IncludeManifest:true),
            Ffmpeg, Ffprobe, new Progress<int>(), default);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(full, ".json")));
        Assert.Equal(4, manifest.RootElement.GetProperty("trajectory").GetProperty("points").GetArrayLength());
        // Sample top-center. It is green midway through the pan, blue after the pan,
        // black in the fit letterbox, then blue after the hard cut back to crop.
        foreach (var (atMs, channel) in new[] {(400,1), (900,2), (1100,-1), (1500,2)})
        {
            var before = await Pixels(preview, atMs - 300);
            var after = await Pixels(full, atMs);
            Assert.InRange(before.Zip(after, (a,b) => Math.Abs(a-b)).Average(), 0, 3);
            var top = after.AsSpan(3*(1*18+9), 3).ToArray();
            if (channel < 0) Assert.All(top, b => Assert.InRange(b, 0, 15));
            else Assert.InRange(top[channel], 220, 255);
        }

        // The bridge sends scalar fields only. Changes to title/style retain the trajectory,
        // manual framing wins and a later trim invalidates even a dormant trajectory.
        var reopened = new Library(root);
        var title = reopened.SaveEdit(projectId, clip.Id, clip.Revision,
            edit with { Title = "Мой кадр", CaptionStyle = edit.Style with { Size = 74 }, Trajectory = null }, null);
        Assert.Equal(path.Points, title.Edit.Trajectory!.Points);
        var manual = reopened.SaveEdit(projectId, clip.Id, title.Revision,
            title.Edit with { Frame = "crop", X = .2, Trajectory = null }, null);
        Assert.Equal(("crop", .2), (manual.Edit.Frame, manual.Edit.X));
        Assert.Equal(path.Points, manual.Edit.Trajectory!.Points);
        Assert.Equal("edit_changed", Assert.Throws<UserError>(() =>
            lib.SaveEdit(projectId, clip.Id, title.Revision, title.Edit, null)).Code);
        var active = reopened.SaveEdit(projectId, clip.Id, manual.Revision,
            manual.Edit with { Frame = "auto", Trajectory = null }, null);
        var trimmed = reopened.SaveEdit(projectId, clip.Id, active.Revision, active.Edit with { StartUs = 350_000 }, null);
        Assert.Equal("fit", trimmed.Edit.Frame);
        Assert.Null(trimmed.Edit.Trajectory);
        Assert.Equal("auto_missing", Assert.Throws<UserError>(() => reopened.SaveEdit(projectId, clip.Id,
            trimmed.Revision, trimmed.Edit with { Frame = "auto", Trajectory = path }, null)).Code);
    }

    private async Task<byte[]> Pixels(string file, int ms)
    {
        string raw = Path.Combine(root, Guid.NewGuid().ToString("N") + ".rgb");
        await Run("-ss", (ms/1000d).ToString(CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1",
            "-vf", "scale=18:32", "-pix_fmt", "rgb24", "-f", "rawvideo", raw);
        return await File.ReadAllBytesAsync(raw);
    }

    private static Task<string> Run(params string[] args) =>
        MediaProcess.RunAsync(Ffmpeg, ["-v","error","-nostdin", ..args], TimeSpan.FromSeconds(30), 0, default);

    public void Dispose() => Directory.Delete(root, true);
}
