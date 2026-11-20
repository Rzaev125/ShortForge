using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record MediaProject(Guid Id, string Name, DateTimeOffset UpdatedUtc, long DurationUs,
    int Width, int Height, string VideoCodec, string Container, int AudioTracks, string? Fps,
    double Rotation, bool SourceAvailable, string OriginalPath, string? ThumbnailPath, string? ProxyPath, bool ProxyNeedsRefresh = false)
{
    public int ProxyAudioTrack { get; init; }
    public Guid? ProxyId { get; init; }
    public int[] OfflineAudioTracks { get; init; } = [];
    public VideoColor? Color { get; init; }
    public FrameIntervals? FrameIntervals { get; init; }
    public Transcript? Transcript { get; init; }
    public ClipSelection? Selection { get; init; }
    public EditSummary[] Edits { get; init; } = [];
    public AnamorphicAspect? AnamorphicAspect { get; init; }
    public string[] AudioTrackLabels { get; init; } = [];
}

public sealed record ImportProgress(string Stage, int? Percent = null);

public sealed partial class Library
{
    private readonly string database;
    private readonly string thumbnailDirectory;
    private readonly string proxyDirectory;
    private readonly string asrWorkDirectory;

    public Library(string dataDirectory)
    {
        var root = Directory.CreateDirectory(dataDirectory).FullName;
        thumbnailDirectory = Path.Combine(root, "thumbnails");
        proxyDirectory = Path.Combine(root, "proxies");
        asrWorkDirectory = Path.Combine(root, "asr-work");
        database = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "library.sqlite3"), ForeignKeys = true, Pooling = false,
        }.ToString();
        using var connection = Connect();
        using var tx = connection.BeginTransaction();
        using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > 9)
            throw new UserError("database_newer", "База проектов создана более новой версией ShortForge.");
        if (version == 0)
        {
            query.CommandText = """
                CREATE TABLE project (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                    settings_version INTEGER NOT NULL DEFAULT 1, settings_json TEXT NOT NULL DEFAULT '{}'
                );
                CREATE TABLE media_asset (
                    project_id TEXT PRIMARY KEY REFERENCES project(id) ON DELETE CASCADE,
                    original_path TEXT NOT NULL, size_bytes INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
                    sha256 TEXT NOT NULL, streams_json TEXT NOT NULL, duration_us INTEGER NOT NULL CHECK(duration_us > 0),
                    width INTEGER NOT NULL, height INTEGER NOT NULL, video_codec TEXT NOT NULL, container TEXT NOT NULL,
                    audio_tracks INTEGER NOT NULL, fps TEXT, rotation REAL NOT NULL, has_thumbnail INTEGER NOT NULL
                );
                PRAGMA user_version = 1;
                """;
            query.ExecuteNonQuery();
        }
        if (version < 2)
        {
            query.CommandText = "ALTER TABLE media_asset ADD COLUMN proxy_id TEXT; PRAGMA user_version = 2;";
            query.ExecuteNonQuery();
        }
        if (version < 3)
        {
            query.CommandText = """
                CREATE TABLE proxy_track (
                    project_id TEXT NOT NULL REFERENCES project(id) ON DELETE CASCADE,
                    audio_ordinal INTEGER NOT NULL CHECK(audio_ordinal >= 0),
                    proxy_id TEXT NOT NULL UNIQUE, PRIMARY KEY(project_id, audio_ordinal)
                );
                INSERT INTO proxy_track SELECT project_id,0,proxy_id FROM media_asset WHERE proxy_id IS NOT NULL;
                PRAGMA user_version = 3;
                """;
            query.ExecuteNonQuery();
        }
        if (version < 4)
        {
            query.CommandText = """
                CREATE TABLE frame_sample (
                    project_id TEXT PRIMARY KEY REFERENCES project(id) ON DELETE CASCADE,
                    frames INTEGER NOT NULL CHECK(frames BETWEEN 2 AND 48),
                    min_ms REAL NOT NULL CHECK(min_ms > 0), max_ms REAL NOT NULL CHECK(max_ms >= min_ms)
                );
                PRAGMA user_version = 4;
                """;
            query.ExecuteNonQuery();
        }
        if (version < 5)
        {
            query.CommandText = """
                CREATE TABLE transcript_cache (
                    project_id TEXT NOT NULL REFERENCES project(id) ON DELETE CASCADE,
                    cache_key TEXT NOT NULL, selected INTEGER NOT NULL, result_json TEXT NOT NULL,
                    PRIMARY KEY(project_id,cache_key)
                );
                CREATE UNIQUE INDEX transcript_selected ON transcript_cache(project_id) WHERE selected=1;
                PRAGMA user_version=5;
                """;
            query.ExecuteNonQuery();
        }
        if (version < 6)
        {
            query.CommandText = "ALTER TABLE transcript_cache ADD COLUMN clips_json TEXT; PRAGMA user_version=6;";
            query.ExecuteNonQuery();
        }
        if (version < 7)
        {
            query.CommandText = """
                CREATE TABLE edit_clip (
                    id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES project(id) ON DELETE CASCADE,
                    selection_id TEXT, start_id INTEGER, transcript_key TEXT, audio_track INTEGER NOT NULL,
                    revision INTEGER NOT NULL, decision TEXT NOT NULL, UNIQUE(project_id,selection_id,start_id)
                );
                CREATE TABLE edit_text (
                    clip_id TEXT NOT NULL REFERENCES edit_clip(id) ON DELETE CASCADE,
                    segment_index INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY(clip_id,segment_index)
                );
                PRAGMA user_version=7;
                """;
            query.ExecuteNonQuery();
        }
        if (version < 8)
        {
            query.CommandText = """
                ALTER TABLE edit_text ADD COLUMN source_start_us INTEGER;
                ALTER TABLE edit_text ADD COLUMN source_end_us INTEGER;
                PRAGMA user_version=8;
                """;
            query.ExecuteNonQuery();
        }
        if (version < 9)
        {
            query.CommandText = ExportQueueMigration;
            query.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(database);
        connection.Open();
        return connection;
    }

    public IReadOnlyList<MediaProject> List() => ReadProjects(null);
    public MediaProject Open(Guid id)
    {
        var project = ReadProjects(id).SingleOrDefault() ?? throw MissingProject();
        project = ReadTranscript(project) with { Edits = ListEdits(id) };
        if (project.SourceAvailable || project.AudioTracks < 2) return project;
        return project with { OfflineAudioTracks = ReadOfflineAudioTracks(id, project.AudioTracks) };
    }
    private static UserError MissingProject() => new("project_missing", "Проект не найден.");

    private List<MediaProject> ReadProjects(Guid? filter)
    {
        using var connection = Connect();
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT p.id,p.name,p.updated_utc,m.duration_us,m.width,m.height,m.video_codec,m.container,
                   m.audio_tracks,m.fps,m.rotation,m.original_path,m.size_bytes,m.mtime_ticks,m.has_thumbnail,m.proxy_id,
                   COALESCE(t.audio_ordinal,0), CASE WHEN $details THEN m.streams_json END,
                   s.frames,s.min_ms,s.max_ms
            FROM project AS p JOIN media_asset AS m ON m.project_id=p.id
            LEFT JOIN frame_sample AS s ON s.project_id=p.id
            LEFT JOIN proxy_track AS t ON t.project_id=p.id AND t.proxy_id=m.proxy_id
            """ + (filter.HasValue ? " WHERE p.id=$id" : " ORDER BY p.updated_utc DESC, p.id");
        query.Parameters.AddWithValue("$details", filter.HasValue);
        if (filter.HasValue) query.Parameters.AddWithValue("$id", filter.Value.ToString("D"));
        using var reader = query.ExecuteReader();
        var projects = new List<MediaProject>();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0));
            var path = reader.GetString(11);
            var thumbnail = ThumbnailPath(id);
            var proxy = ExistingProxy(reader.IsDBNull(15) ? null : reader.GetString(15));
            var project = new MediaProject(id, reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2),
                    CultureInfo.InvariantCulture), reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetDouble(10), MatchesFileInfo(path, reader.GetInt64(12), reader.GetInt64(13)), path,
                reader.GetBoolean(14) && File.Exists(thumbnail) ? thumbnail : null,
                proxy.Path, proxy.NeedsRefresh)
            {
                FrameIntervals = reader.IsDBNull(18) ? null : new(reader.GetInt32(18), reader.GetDouble(19), reader.GetDouble(20)),
                ProxyAudioTrack = reader.GetInt32(16),
                ProxyId = proxy.Path == null ? null : Guid.ParseExact(reader.GetString(15), "N")
            };
            projects.Add(reader.IsDBNull(17) ? project : WithStreamDetails(project, reader.GetString(17)));
        }
        return projects;
    }

    private static MediaProject WithStreamDetails(MediaProject project, string json)
    {
        try
        {
            using var probe = JsonDocument.Parse(json);
            var streams = probe.RootElement.GetProperty("streams");
            var video = MediaProbe.SelectVideo(streams.EnumerateArray());
            return project with
            {
                Color = MediaProbe.ReadColor(video),
                AnamorphicAspect = MediaProbe.ReadAnamorphicAspect(video),
                AudioTrackLabels = project.AudioTracks > 1 ? ReadAudioLabels(streams) : []
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // The saved path, scalar metadata and media remain usable if the probe snapshot is damaged.
            return project;
        }
    }

    private static string[] ReadAudioLabels(JsonElement streams)
    {
        static string? Label(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() : null;
        var labels = new List<string>();
        foreach (var stream in streams.EnumerateArray())
        {
            if (Label(stream, "codec_type") != "audio") continue;
            string? title = null, language = null;
            if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                title = Label(tags, "title");
                language = Label(tags, "language");
            }
            var parts = new[] { title, language == "und" ? null : language, Label(stream, "codec_name") };
            labels.Add(string.Join(" · ", parts.OfType<string>().Where(p => p.Length > 0).Select(p => p[..Math.Min(100, p.Length)])));
        }
        return labels.ToArray();
    }

    public async Task<MediaProject> ImportAsync(string path, string probe, string? ffmpeg, CancellationToken cancellationToken,
        IProgress<ImportProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = OpenSource(path);
        var media = await MediaProbe.ReadAsync(probe, source.Name, cancellationToken);
        var fingerprint = await FingerprintAsync(source, progress, cancellationToken);
        var id = Guid.NewGuid();
        string? thumbnail = null;
        try
        {
            if (!media.IsHdr && !string.IsNullOrWhiteSpace(ffmpeg))
            {
                progress?.Report(new("thumbnail"));
                thumbnail = await CreateThumbnailAsync(id, source.Name, media.StreamIndex, ffmpeg, probe, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("save"));
            using var connection = Connect();
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO project (id,name,created_utc,updated_utc) VALUES ($id,$name,$now,$now);
                INSERT INTO media_asset (project_id,original_path,size_bytes,mtime_ticks,sha256,streams_json,
                    duration_us,width,height,video_codec,container,audio_tracks,fps,rotation,has_thumbnail)
                VALUES ($id,$path,$size,$mtime,$hash,$json,$duration,$width,$height,$codec,$container,$audio,$fps,$rotation,$thumb);
                """;
            var parameters = command.Parameters;
            parameters.AddWithValue("$id", id.ToString("D"));
            parameters.AddWithValue("$name", Path.GetFileNameWithoutExtension(source.Name));
            parameters.AddWithValue("$now", UtcNow());
            parameters.AddWithValue("$path", source.Name);
            parameters.AddWithValue("$size", source.Length);
            parameters.AddWithValue("$mtime", File.GetLastWriteTimeUtc(source.Name).Ticks);
            parameters.AddWithValue("$hash", fingerprint);
            parameters.AddWithValue("$json", media.Json);
            parameters.AddWithValue("$duration", media.DurationUs);
            parameters.AddWithValue("$width", media.Width);
            parameters.AddWithValue("$height", media.Height);
            parameters.AddWithValue("$codec", media.VideoCodec);
            parameters.AddWithValue("$container", media.Container);
            parameters.AddWithValue("$audio", media.AudioTracks);
            parameters.AddWithValue("$fps", (object?)media.Fps ?? DBNull.Value);
            parameters.AddWithValue("$rotation", media.Rotation);
            parameters.AddWithValue("$thumb", thumbnail is not null);
            command.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch
        {
            if (thumbnail is not null) File.Delete(thumbnail);
            throw;
        }
        return Open(id);
    }

    // Keep the returned read handle alive while the child process uses its Name.
    // FileShare.Read prevents replacement or modification during processing on Windows.
    public Task<FileStream> OpenOriginalForProcessingAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = ReadOriginal(id);
        return OpenMatchingOriginalAsync(original.Path, original.Size, original.Hash, cancellationToken);
    }

    private (string Path, long Size, string Hash) ReadOriginal(Guid id)
    {
        using var connection = Connect();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT original_path,size_bytes,sha256 FROM media_asset WHERE project_id=$id";
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw MissingProject();
        return (reader.GetString(0), reader.GetInt64(1), reader.GetString(2));
    }

    private static async Task<string> FingerprintAsync(FileStream source, IProgress<ImportProgress>? progress, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var length = source.Length;
        var reported = 0;
        progress?.Report(new("fingerprint", 0));
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            if (progress == null) continue;
            int percent = (int)(source.Position * 100.0 / length);
            if (percent >= reported + 5 || percent == 100)
            {
                progress.Report(new("fingerprint", percent));
                reported = percent;
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<FileStream> OpenMatchingOriginalAsync(string path, long size, string expected, CancellationToken token)
    {
        var source = OpenSource(path);
        try
        {
            if (source.Length != size) throw DifferentSource();
            var actual = await FingerprintAsync(source, null, token);
            if (actual != expected) throw DifferentSource();
            source.Position = 0;
            return source;
        }
        catch { source.Dispose(); throw; }
    }

    public async Task<MediaProject> RelinkAsync(Guid id, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = ReadOriginal(id);
        using var source = await OpenMatchingOriginalAsync(path, original.Size, original.Hash, cancellationToken);
        using var connection = Connect();
        using var tx = connection.BeginTransaction();
        using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            UPDATE media_asset SET original_path=$path, mtime_ticks=$mtime WHERE project_id=$id;
            UPDATE project SET updated_utc=$now WHERE id=$id;
            """;
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        query.Parameters.AddWithValue("$path", source.Name);
        query.Parameters.AddWithValue("$mtime", File.GetLastWriteTimeUtc(source.Name).Ticks);
        query.Parameters.AddWithValue("$now", UtcNow());
        query.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
        return Open(id);
    }

    public static async Task<string?> ToolVersionAsync(string exe, CancellationToken cancellationToken)
    {
        try
        {
            var version = await MediaProcess.RunAsync(exe, ["-version"], TimeSpan.FromSeconds(5), 64 * 1024, cancellationToken);
            var first = version.Split(['\r', '\n'], 2)[0].Trim();
            var tokens = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 3 && tokens[1] == "version"
                && tokens[0] is "ffmpeg" or "ffprobe" ? string.Join(" ", tokens.Take(3)) : null;
        }
        catch (UserError) { return null; }
    }

    private string ThumbnailPath(Guid id) => Path.Combine(thumbnailDirectory, id.ToString("N") + ".jpg");

    private async Task<string?> CreateThumbnailAsync(Guid id, string source, int stream, string ffmpeg, string probe,
        CancellationToken token)
    {
        Directory.CreateDirectory(thumbnailDirectory);
        var target = ThumbnailPath(id);
        var temporary = Path.ChangeExtension(target, ".partial.jpg");
        try
        {
            await MediaProcess.RunAsync(ffmpeg,
                ["-nostdin", "-v", "error", "-n", "-protocol_whitelist", "file", "-format_whitelist",
                 MediaProbe.StandaloneFormats, "-threads", "2", "-i", source,
                 "-map", "0:" + stream.ToString(CultureInfo.InvariantCulture), "-frames:v", "1",
                 "-vf", "scale=320:180:force_original_aspect_ratio=decrease:reset_sar=1", "-threads", "1", "-c:v", "mjpeg",
                 "-f", "image2", "-update", "1", temporary], TimeSpan.FromSeconds(30), 8192, token);
            // A zero exit code alone does not establish that a decodable image exists.
            var codec = await MediaProcess.RunAsync(probe, ["-v", "error", "-protocol_whitelist", "file", "-select_streams",
                "v:0", "-show_entries", "stream=codec_name,width,height", "-of", "csv=p=0", "-i", temporary],
                TimeSpan.FromSeconds(10), 4096, token);
            var fields = codec.Trim().Split(',');
            if (fields.Length != 3 || fields[0] != "mjpeg" || !int.TryParse(fields[1], out var width)
                || !int.TryParse(fields[2], out var height) || width is <= 0 or > 320 || height is <= 0 or > 180)
                return null;
            File.Move(temporary, target);
            return target;
        }
        catch (UserError) { return null; }
        finally { File.Delete(temporary); }
    }

    private static FileStream OpenSource(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException();
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new UserError("source_unavailable", "Не удалось открыть исходный файл. Проверьте его расположение и доступ."); }
    }

    private static bool MatchesFileInfo(string path, long size, long mtime)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == mtime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static UserError DifferentSource() => new("source_mismatch", "Выбранный файл отличается от исходного видео проекта.");
    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
