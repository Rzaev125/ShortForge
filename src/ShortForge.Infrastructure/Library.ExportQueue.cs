using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

// The bridge can serialize this record directly. Destination and source paths stay in the host.
public sealed record ExportJob(Guid Id, Guid ProjectId, Guid ClipId, int Revision, string Title,
    string FileName, ExportSettings Settings, string State, int Percent, string? Error);

public sealed partial class Library
{
    private const string ExportQueueMigration = """
        CREATE TABLE export_job (
            id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES project(id) ON DELETE CASCADE,
            clip_id TEXT NOT NULL REFERENCES edit_clip(id) ON DELETE CASCADE, revision INTEGER NOT NULL,
            title TEXT NOT NULL, destination TEXT NOT NULL UNIQUE, fps INTEGER NOT NULL CHECK(fps IN (30,60)),
            normalize INTEGER NOT NULL, srt INTEGER NOT NULL, manifest INTEGER NOT NULL, prefer_nvenc INTEGER NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('queued','running','completed','failed','cancelled','interrupted')),
            percent INTEGER NOT NULL DEFAULT 0 CHECK(percent BETWEEN 0 AND 100),
            error TEXT, cancel_requested INTEGER NOT NULL DEFAULT 0, verified_files TEXT,
            cleanup_pending INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX export_job_state ON export_job(state);
        PRAGMA user_version=9;
        """;

    private const string JobColumns = """
        id,project_id,clip_id,revision,title,destination,fps,normalize,srt,manifest,prefer_nvenc,state,percent,error
        """;
    private sealed record StoredJob(ExportJob Job, string Destination);

    private static StoredJob ReadJob(SqliteDataReader row)
    {
        string destination = row.GetString(5);
        return new(new(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)), Guid.Parse(row.GetString(2)),
            row.GetInt32(3), row.GetString(4), Path.GetFileName(destination),
            new(row.GetInt32(6), row.GetBoolean(7), row.GetBoolean(8), row.GetBoolean(9), row.GetBoolean(10)),
            row.GetString(11), row.GetInt32(12), row.IsDBNull(13) ? null : row.GetString(13)), destination);
    }

    public IReadOnlyList<ExportJob> EnqueueExports(Guid projectId, IReadOnlyList<Guid> clipIds, string directory,
        ExportSettings settings)
    {
        if (clipIds.Count is < 1 or > 100 || clipIds.Distinct().Count() != clipIds.Count)
            throw new UserError("export_selection", "Выберите от 1 до 100 разных сохранённых клипов.");
        if (settings.Fps is not (30 or 60))
            throw new UserError("export_fps", "Выберите 30 или 60 кадров в секунду.");
        if (!IsLocalExportPath(directory) || !Directory.Exists(directory))
            throw new UserError("export_folder", "Выберите существующую локальную папку для экспорта.");
        directory = Path.GetFullPath(directory);
        var jobs = new List<ExportJob>(clipIds.Count);
        using var db = Connect();
        using var tx = db.BeginTransaction();
        foreach (var clip in clipIds)
        {
            var id = Guid.NewGuid();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
            cmd.Parameters.AddWithValue("$clip", clip.ToString("D"));
            cmd.CommandText = """
                SELECT revision,CASE WHEN json_valid(decision) THEN json_extract(decision,'$.Title') END
                FROM edit_clip WHERE project_id=$project AND id=$clip;
                """;
            int revision;
            string title;
            using (var row = cmd.ExecuteReader())
            {
                if (!row.Read()) throw ChangedEdit();
                revision = row.GetInt32(0);
                title = row.IsDBNull(1) ? "Клип" : row.GetString(1);
            }
            string destination = ExportDestination(directory, title, id);
            cmd.CommandText = """
                INSERT INTO export_job(id,project_id,clip_id,revision,title,destination,fps,normalize,srt,manifest,prefer_nvenc,state)
                VALUES($id,$project,$clip,$revision,$title,$destination,$fps,$normalize,$srt,$manifest,$nvenc,'queued');
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            cmd.Parameters.AddWithValue("$revision", revision);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$destination", destination);
            cmd.Parameters.AddWithValue("$fps", settings.Fps);
            cmd.Parameters.AddWithValue("$normalize", settings.NormalizeAudio);
            cmd.Parameters.AddWithValue("$srt", settings.IncludeSrt);
            cmd.Parameters.AddWithValue("$manifest", settings.IncludeManifest);
            cmd.Parameters.AddWithValue("$nvenc", settings.PreferNvenc);
            cmd.ExecuteNonQuery();
            jobs.Add(new(id, projectId, clip, revision, title, Path.GetFileName(destination), settings, "queued", 0, null));
        }
        tx.Commit();
        return jobs;
    }

    public IReadOnlyList<ExportJob> ListExportJobs()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {JobColumns} FROM export_job ORDER BY rowid DESC";
        using var rows = cmd.ExecuteReader();
        var jobs = new List<ExportJob>();
        while (rows.Read()) jobs.Add(ReadJob(rows).Job);
        return jobs;
    }

    public bool CancelExport(Guid jobId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE export_job SET cancel_requested=1, state=CASE state WHEN 'queued' THEN 'cancelled' ELSE state END
            WHERE id=$id AND state IN ('queued','running') AND cancel_requested=0
            """;
        cmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool RetryExport(Guid jobId)
    {
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
        cmd.CommandText = """
            SELECT j.destination,j.title,j.revision,e.revision FROM export_job j
            JOIN edit_clip e ON e.id=j.clip_id AND e.project_id=j.project_id
            WHERE j.id=$id AND j.state IN ('failed','cancelled','interrupted')
            """;
        string destination, title;
        using (var row = cmd.ExecuteReader())
        {
            if (!row.Read()) return false;
            if (row.GetInt32(2) != row.GetInt32(3)) throw OutdatedExport();
            destination = row.GetString(0);
            title = row.GetString(1);
        }
        if (!IsStoredExportDestination(destination, jobId))
            throw new UserError("export_destination", "Путь задания повреждён. Добавьте клип в очередь и выберите папку заново.");
        string folder = Path.GetDirectoryName(destination)!;
        // Preserve a directory whose ownership marker could not be written or verified.
        if (Path.Exists(folder) || Path.Exists(ExportDirectories(destination, jobId).Stage))
            destination = ExportDestination(Path.GetDirectoryName(folder)!, title, Guid.NewGuid());
        cmd.CommandText = "UPDATE export_job SET destination=$destination,state='queued',percent=0,cancel_requested=0,error=NULL,verified_files=NULL WHERE id=$id";
        cmd.Parameters.AddWithValue("$destination", destination);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public string ExportFolder(Guid jobId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT destination FROM export_job WHERE id=$id AND state='completed'";
        cmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
        if (cmd.ExecuteScalar() is not string path || !IsStoredExportDestination(path, jobId) || !File.Exists(path))
            throw new UserError("export_unavailable", "Готовый файл экспорта недоступен.");
        return Path.GetDirectoryName(path)!;
    }

    // Every wakeup waits for the owner and checks the persisted queue, even when an earlier drain just ended.
    public Task RunExportQueueAsync(Func<(string Ffmpeg, string Ffprobe)> resolveTools, CancellationToken hostLifetime,
        SemaphoreSlim? heavyGate = null) => Task.Run(async () =>
    {
        try
        {
            using var ownership = await OwnExportQueueAsync(hostLifetime);
            await RecoverExportQueueAsync(hostLifetime);
            while (!hostLifetime.IsCancellationRequested && HasQueuedExport())
            {
                if (heavyGate != null) await heavyGate.WaitAsync(hostLifetime);
                try
                {
                    hostLifetime.ThrowIfCancellationRequested();
                    if (ClaimExport() is {} next) await ProcessExportAsync(next, resolveTools, hostLifetime);
                }
                finally { heavyGate?.Release(); }
            }
        }
        catch (OperationCanceledException) when (hostLifetime.IsCancellationRequested) { }
    });

    private async Task<FileStream> OwnExportQueueAsync(CancellationToken token)
    {
        string file = Path.Combine(Path.GetDirectoryName(proxyDirectory)!, "export-worker.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(125, token); }
        }
    }

    private bool HasQueuedExport()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM export_job WHERE state='queued' LIMIT 1";
        return cmd.ExecuteScalar() != null;
    }

    private StoredJob? ClaimExport()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            UPDATE export_job SET state='running',cleanup_pending=1 WHERE id=(
                SELECT id FROM export_job WHERE state='queued' ORDER BY rowid LIMIT 1
            ) RETURNING {JobColumns}
            """;
        using var row = cmd.ExecuteReader();
        return row.Read() ? ReadJob(row) : null;
    }

    // Store fingerprints only after ffprobe has verified the staged bundle. The OS rename can succeed
    // just before a crash rolls back the SQLite transaction; the final bytes then prove publication.
    private async Task PrepareExportCompletionAsync(Guid id, string temporaryMp4, CancellationToken token)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.CommandText = "SELECT 1 FROM export_job WHERE id=$id AND state='running'";
        if (cmd.ExecuteScalar() == null) return;
        string proof = await FingerprintBundleAsync(temporaryMp4, token);
        cmd.CommandText = "UPDATE export_job SET verified_files=$proof WHERE id=$id AND state='running'";
        cmd.Parameters.AddWithValue("$proof", proof);
        cmd.ExecuteNonQuery();
    }

    private static async Task<string> FingerprintBundleAsync(string mp4, CancellationToken token)
    {
        var hashes = new List<string?>(3);
        foreach (string extension in new[] { ".mp4", ".srt", ".json" })
        {
            string path = Path.ChangeExtension(mp4, extension);
            if (extension != ".mp4" && !File.Exists(path)) { hashes.Add(null); continue; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
        }
        return JsonSerializer.Serialize(hashes);
    }

    private async Task RecoverExportQueueAsync(CancellationToken token)
    {
        var stale = new List<(Guid Id, string Destination, string? Proof, string State)>();
        using (var db = Connect())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id,destination,verified_files,state FROM export_job WHERE state='running' OR cleanup_pending=1";
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) stale.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1),
                rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetString(3)));
        }
        foreach (var (id, destination, proof, state) in stale)
        {
            token.ThrowIfCancellationRequested();
            CleanInterruptedExport(destination, id);
            RecordCleanup(id, destination);
            if (state != "running") continue;
            bool published = await MatchesPublishedBundleAsync(id, destination, proof, token);
            FinishExport(id, published ? "completed" : "interrupted", published ? null : InterruptedExport);
        }
    }

    private const string InterruptedExport = "Экспорт прерван при закрытии приложения. Задание можно повторить."; 
    private static async Task<bool> MatchesPublishedBundleAsync(Guid id, string destination, string? proof, CancellationToken token)
    {
        if (proof == null || !IsStoredExportDestination(destination, id) || !File.Exists(destination)) return false;
        try { return proof == await FingerprintBundleAsync(destination, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private string? VerifiedFiles(Guid jobId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT verified_files FROM export_job WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", jobId.ToString("D"));
        return cmd.ExecuteScalar() as string;
    }

    private static UserError OutdatedExport() => new("export_outdated",
        "Монтаж клипа изменился. Выберите этот клип в проекте и добавьте его актуальную версию в очередь.");

    private void RecordCleanup(Guid id, string destination)
    {
        if (IsStoredExportDestination(destination, id) && Directory.Exists(ExportDirectories(destination, id).Stage)) return;
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE export_job SET cleanup_pending=0 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    private static bool IsStoredExportDestination(string destination, Guid id)
    {
        try { _ = ExportDirectories(destination, id); return true; }
        catch (Exception e) when (e is UserError or ArgumentException or FormatException or NotSupportedException) { return false; }
    }

    private sealed class ExportPercent : IProgress<int>
    {
        private int latest;
        public int Latest => Volatile.Read(ref latest);
        public void Report(int value) => Volatile.Write(ref latest, Math.Clamp(value, 0, 99));
    }

    private async Task ProcessExportAsync(StoredJob next, Func<(string Ffmpeg, string Ffprobe)> resolveTools,
        CancellationToken hostLifetime)
    {
        var job = next.Job;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime);
        try
        {
            var tools = resolveTools();
            var progress = new ExportPercent();
            CleanInterruptedExport(next.Destination, job.Id);
            var render = RenderExportJobAsync(job.ProjectId, job.ClipId, job.Revision, job.Id, next.Destination,
                job.Settings, tools.Ffmpeg, tools.Ffprobe, progress, stop.Token);
            try
            {
                while (!render.IsCompleted)
                {
                    if (PollExport(job.Id, progress.Latest))
                    {
                        stop.Cancel();
                        break;
                    }
                    await Task.WhenAny(render, Task.Delay(200));
                }
            }
            catch
            {
                stop.Cancel();
                try { await render; } catch (OperationCanceledException) { }
                throw;
            }
            await render;
            FinishExport(job.Id, "completed", null);
        }
        catch (OperationCanceledException)
        {
            bool shutdown = hostLifetime.IsCancellationRequested;
            FinishExport(job.Id, shutdown ? "interrupted" : "cancelled",
                shutdown ? InterruptedExport : null);
        }
        catch (Exception error)
        {
            string message = error switch
            {
                UserError { Code: "edit_changed" } => OutdatedExport().Message,
                UserError known => known.Message,
                UnauthorizedAccessException => "Нет прав на запись в папку экспорта.",
                IOException => "Не удалось записать экспорт. Проверьте свободное место и доступ к папке.",
                _ => "Не удалось сохранить клип. Проверьте настройки экспорта и повторите задание."
            };
            // Rename may have succeeded while SQLite refused COMMIT. If the second write also fails,
            // leave the durable running row for startup recovery instead of enabling a duplicate retry.
            if (File.Exists(next.Destination) &&
                await MatchesPublishedBundleAsync(job.Id, next.Destination, VerifiedFiles(job.Id), CancellationToken.None))
                FinishExport(job.Id, "completed", null);
            else
                FinishExport(job.Id, "failed", message);
        }
        finally { RecordCleanup(job.Id, next.Destination); }
    }

    private bool PollExport(Guid id, int percent)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE export_job SET percent=$percent WHERE id=$id AND state='running' AND percent<$percent;
            SELECT cancel_requested FROM export_job WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$percent", percent);
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private void FinishExport(Guid id, string state, string? error)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE export_job SET state=CASE WHEN $state='interrupted' AND cancel_requested=1 THEN 'cancelled' ELSE $state END,
                error=CASE WHEN $state='interrupted' AND cancel_requested=1 THEN NULL ELSE $error END,
                percent=CASE WHEN $state='completed' THEN 100 ELSE percent END
            WHERE id=$id AND state='running'
            """;
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.ExecuteNonQuery();
    }
}
