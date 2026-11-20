using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public sealed class ExportQueueTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("Очередь O'Brien ").FullName;
    private string Data => Path.Combine(root, "library");
    private string Output => Directory.CreateDirectory(Path.Combine(root, "Мои экспорты")).FullName;
    private static (string Ffmpeg,string Ffprobe) Tools() => (LibraryTests.Ffmpeg!, LibraryTests.Ffprobe!);

    private async Task<(Library Library, Guid Project, string Source)> Import(int seconds = 1)
    {
        string source = Path.Combine(root, "Мой фильм " + new string('а', 72) + ".mp4");
        await MediaProcess.RunAsync(Tools().Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=192x108:r=30",
            "-f", "lavfi", "-i", "sine=frequency=330", "-t", seconds.ToString(), "-c:v", "libx264", "-c:a", "aac", source],
            TimeSpan.FromSeconds(30), 0, default);
        var db = new Library(Data);
        return (db, (await db.ImportAsync(source, Tools().Ffprobe, null, default)).Id, source);
    }

    [RealMediaFact]
    public async Task BrokenJobAndOccupiedDestinationDoNotBlockOtherJobsAndRetryNeverOverwrites()
    {
        var (db, project, source) = await Import();
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var clips = Enumerable.Range(0, 4).Select(_ => db.CreateEdit(project)).ToArray();
        var settings = new ExportSettings(30, false, false, true);
        var jobs = db.EnqueueExports(project, clips.Select(c => c.Id).ToArray(), Output, settings);
        Assert.Equal(4, jobs.Count);
        Assert.True(db.CancelExport(jobs[0].Id));
        Assert.False(db.CancelExport(jobs[0].Id));
        // The second clip changed after enqueue. Its queued revision must never render silently.
        db.SaveEdit(project, clips[1].Id, clips[1].Revision, clips[1].Edit with { Title = "Правка" }, null);
        var occupied = Path.GetDirectoryName(Library.ExportDestination(Output, clips[2].Edit.Title, jobs[2].Id))!;
        using (var locked = new FileStream(occupied, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            locked.WriteByte(42);
            await Task.WhenAll(db.RunExportQueueAsync(Tools, default), new Library(Data).RunExportQueueAsync(Tools, default));
        }
        Assert.Equal(new byte[] {42}, await File.ReadAllBytesAsync(occupied));
        var persisted = new Library(Data);
        var rows = persisted.ListExportJobs().ToDictionary(j => j.Id);
        Assert.Equal("cancelled", rows[jobs[0].Id].State);
        Assert.Equal("failed", rows[jobs[1].Id].State);
        Assert.Equal("export_outdated", Assert.Throws<UserError>(() => persisted.RetryExport(jobs[1].Id)).Code);
        Assert.Equal("failed", rows[jobs[2].Id].State);
        Assert.Equal("completed", rows[jobs[3].Id].State);
        Assert.Equal(100, rows[jobs[3].Id].Percent);
        Assert.DoesNotContain(root, JsonSerializer.Serialize(rows), StringComparison.OrdinalIgnoreCase);
        Assert.True(persisted.RetryExport(jobs[2].Id));
        Assert.False(persisted.RetryExport(jobs[2].Id));
        await persisted.RunExportQueueAsync(Tools, default);
        var retried = persisted.ListExportJobs().Single(j => j.Id == jobs[2].Id);
        Assert.Equal("completed", retried.State);
        var probe = await MediaProbe.ReadAsync(Tools().Ffprobe, Path.Combine(persisted.ExportFolder(retried.Id), retried.FileName), default);
        Assert.Equal((1080, 1920, "h264", "aac"), (probe.Width, probe.Height, probe.VideoCodec, probe.FirstAudioCodec));
        Assert.Equal(new byte[] {42}, await File.ReadAllBytesAsync(occupied));
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    [RealMediaFact]
    public async Task BatchIsAtomicAndShutdownWhileWaitingLeavesAllQueued()
    {
        var (db, project, _) = await Import();
        var clip = db.CreateEdit(project);
        Assert.Throws<UserError>(() => db.EnqueueExports(project, [clip.Id,Guid.NewGuid()], Output, new()));
        Assert.Throws<UserError>(() => db.EnqueueExports(project, [clip.Id,clip.Id], Output, new()));
        Assert.Throws<UserError>(() => db.EnqueueExports(project, [clip.Id], Output, new(25)));
        Assert.Empty(db.ListExportJobs());
        var job = Assert.Single(db.EnqueueExports(project, [clip.Id], Output, new()));
        using var gate = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource(200);
        await db.RunExportQueueAsync(Tools, stop.Token, gate);
        Assert.Equal("queued", Assert.Single(db.ListExportJobs()).State);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE export_job SET state='running' WHERE id=$id";
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        command.ExecuteNonQuery();
        var next = Assert.Single(db.EnqueueExports(project, [clip.Id], Output, new()));
        var stage = Directory.CreateDirectory(Path.Combine(Output, $".shortforge-{job.Id:N}"));
        await File.WriteAllBytesAsync(Path.Combine(stage.FullName, "owner"), job.Id.ToByteArray());
        var partial = stage.CreateSubdirectory("result");
        await File.WriteAllBytesAsync(Path.Combine(partial.FullName, job.FileName), [0, 1, 2]);
        string unrelated = Directory.CreateDirectory(Path.Combine(Output, ".shortforge-unrelated")).FullName;
        await File.WriteAllTextAsync(Path.Combine(unrelated, "чужой.txt"), "сохранить");
        var reopened = new Library(Data);
        await reopened.RunExportQueueAsync(Tools, default);
        var result = reopened.ListExportJobs().ToDictionary(j => j.Id);
        Assert.Equal("interrupted", result[job.Id].State);
        Assert.Equal("completed", result[next.Id].State);
        Assert.False(Directory.Exists(stage.FullName));
        Assert.True(File.Exists(Path.Combine(unrelated, "чужой.txt")));
        Assert.True(reopened.RetryExport(job.Id));
    }

    [RealMediaFact]
    public async Task CrashAfterAtomicRenameRecoversOnlyMatchingVerifiedBundle()
    {
        var (db, project, _) = await Import();
        var clip = db.CreateEdit(project);
        var job = Assert.Single(db.EnqueueExports(project, [clip.Id], Output, new(IncludeManifest: true)));
        await db.RunExportQueueAsync(Tools, default);
        var video = Path.Combine(db.ExportFolder(job.Id), job.FileName);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE export_job SET state='running',percent=99 WHERE id=$id AND verified_files IS NOT NULL";
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        Assert.Equal(1, command.ExecuteNonQuery());
        var restarted = new Library(Data);
        await restarted.RunExportQueueAsync(() => throw new Exception("Не должен рендерить заново"), default);
        Assert.Equal("completed", Assert.Single(restarted.ListExportJobs()).State);
        Assert.Equal(1, command.ExecuteNonQuery());
        await File.AppendAllTextAsync(Path.ChangeExtension(video, ".json"), " ");
        await restarted.RunExportQueueAsync(Tools, default);
        Assert.Equal("interrupted", Assert.Single(restarted.ListExportJobs()).State);
        Assert.True(File.Exists(video));
    }

    [RealMediaFact]
    public async Task CrossInstanceCancellationStopsOnlyTheActiveRender()
    {
        var (db, project, _) = await Import(8);
        var first = db.CreateEdit(project);
        var second = db.CreateEdit(project);
        db.SaveEdit(project, second.Id, second.Revision, second.Edit with {EndUs = 400_000}, null);
        var jobs = db.EnqueueExports(project, [first.Id, second.Id], Output, new());
        var run = db.RunExportQueueAsync(Tools, default);
        var another = new Library(Data);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (another.ListExportJobs().Single(j => j.Id == jobs[0].Id).Percent == 0)
        {
            Assert.False(run.IsCompleted);
            await Task.Delay(20, timeout.Token);
        }
        Assert.True(another.CancelExport(jobs[0].Id));
        await run;
        Assert.Equal("cancelled", another.ListExportJobs().Single(j => j.Id == jobs[0].Id).State);
        Assert.DoesNotContain(Directory.GetDirectories(Output), p => Path.GetFileName(p).StartsWith(".shortforge-"));
        Assert.Equal("completed", another.ListExportJobs().Single(j => j.Id == jobs[1].Id).State);
        Assert.Single(Directory.GetFiles(Output, "*.mp4", SearchOption.AllDirectories));
    }

    [RealMediaFact]
    public async Task RetryPreservesUnidentifiedStagingAndGetsFreshWorkingDirectory()
    {
        var (db, project, _) = await Import();
        var clip = db.CreateEdit(project);
        var job = Assert.Single(db.EnqueueExports(project, [clip.Id], Output, new()));
        string stale = Directory.CreateDirectory(Path.Combine(Output, $".shortforge-{job.Id:N}")).FullName;
        // A partial ownership marker can remain if Windows refused the first write.
        byte[] marker = [1, 2, 3];
        await File.WriteAllBytesAsync(Path.Combine(stale, "owner"), marker);
        await db.RunExportQueueAsync(Tools, default);
        Assert.Equal("failed", db.ListExportJobs().Single().State);
        Assert.True(db.RetryExport(job.Id));
        await db.RunExportQueueAsync(Tools, default);
        Assert.Equal("completed", db.ListExportJobs().Single().State);
        Assert.True(File.Exists(Path.Combine(db.ExportFolder(job.Id), job.FileName)));
        Assert.Equal(marker, await File.ReadAllBytesAsync(Path.Combine(stale, "owner")));
    }

    [RealMediaFact]
    public async Task FailedDatabaseCommitAfterRenameKeepsPublishedJobRecoverable()
    {
        var (db, project, _) = await Import();
        var clip = db.CreateEdit(project);
        var job = db.EnqueueExports(project, [clip.Id], Output, new()).Single();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        // This constraint fails at COMMIT after rename, not at the UPDATE before rename.
        cmd.CommandText = """
            CREATE TABLE unavailable(id INTEGER PRIMARY KEY);
            CREATE TABLE deferred_write(id INTEGER REFERENCES unavailable(id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER refuse_completion AFTER UPDATE OF state ON export_job WHEN NEW.state='completed'
            BEGIN INSERT INTO deferred_write VALUES(1); END;
            """;
        cmd.ExecuteNonQuery();
        await Assert.ThrowsAsync<SqliteException>(() => db.RunExportQueueAsync(Tools, default));
        Assert.Single(Directory.GetFiles(Output, "*.mp4", SearchOption.AllDirectories));
        Assert.Equal("running", db.ListExportJobs().Single().State);
        cmd.CommandText = "DROP TRIGGER refuse_completion";
        cmd.ExecuteNonQuery();
        await db.RunExportQueueAsync(() => throw new InvalidOperationException("Unexpected re-encode"), default);
        Assert.Equal("completed", db.ListExportJobs().Single().State);
        Assert.False(db.RetryExport(job.Id));
        Assert.Single(Directory.GetFiles(Output, "*.mp4", SearchOption.AllDirectories));
    }

    [RealMediaFact]
    public async Task CorruptStoredDestinationFailsOnlyItsJob()
    {
        var (db, project, source) = await Import();
        var broken = db.CreateEdit(project);
        var healthy = db.CreateEdit(project);
        var jobs = db.EnqueueExports(project, [broken.Id, healthy.Id], Output, new());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE export_job SET destination=$wrong WHERE id=$id";
        cmd.Parameters.AddWithValue("$wrong", source);
        cmd.Parameters.AddWithValue("$id", jobs[0].Id.ToString("D"));
        cmd.ExecuteNonQuery();
        await db.RunExportQueueAsync(Tools, default);
        var rows = db.ListExportJobs().ToDictionary(j => j.Id);
        Assert.Equal("failed", rows[jobs[0].Id].State);
        Assert.Equal("completed", rows[jobs[1].Id].State);
        Assert.Equal("export_destination", Assert.Throws<UserError>(() => db.RetryExport(jobs[0].Id)).Code);
        var original = await MediaProbe.ReadAsync(Tools().Ffprobe, source, default);
        Assert.Equal((192, 108), (original.Width, original.Height));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
