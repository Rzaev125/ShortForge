using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ShortForge-tests-").FullName;
    private string Data => Path.Combine(root, "data");
    private string Video => Path.Combine(root, "Мой фильм 'тест' " + new string('а', 85) + ".mp4");
    internal static string? Ffmpeg => FindTool("ffmpeg");
    internal static string? Ffprobe => FindTool("ffprobe");

    private static string? FindTool(string name)
    {
        var configured = Environment.GetEnvironmentVariable("SHORTFORGE_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var binary = OperatingSystem.IsWindows() ? name + ".exe" : name;
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            if (!File.Exists(Path.Combine(root.FullName, "Directory.Packages.props"))) continue;
            var local = Path.Combine(root.FullName, "tools", "bin", binary);
            if (File.Exists(local)) return local;
            break;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir.Trim('"'), binary)).FirstOrDefault(File.Exists);
    }

    [RealMediaFact]
    public async Task V7TextSurvivesMigrationAndManualTimingSurvivesTrim()
    {
        await CreateVideo(Video,audio:true);
        var library = new Library(Data);
        var id = (await library.ImportAsync(Video,Ffprobe!,null,default)).Id;
        var original = new Transcript(0,"ru","ru","fixture",1,1,
            [new(0,430_000,"Первый"),new(500_000,800_000,"Второй")]);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource=Path.Combine(Data,"library.sqlite3"),Pooling=false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO transcript_cache(project_id,cache_key,selected,result_json) VALUES($id,'demo',1,$json)";
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$json",System.Text.Json.JsonSerializer.Serialize(original));
        cmd.ExecuteNonQuery();
        var first = library.CreateEdit(id);
        var saved = library.SaveEdit(id,first.Id,first.Revision,first.Edit,new(0,"Ранее сохранено"));
        cmd.CommandText = """
            DROP TABLE export_job;
            ALTER TABLE edit_text DROP COLUMN source_start_us;
            ALTER TABLE edit_text DROP COLUMN source_end_us;
            UPDATE edit_clip SET decision=json_remove(decision,'$.CaptionStyle');
            PRAGMA user_version=7;
            """;
        cmd.ExecuteNonQuery();
        library = new Library(Data);
        var migrated = library.OpenEdit(id,first.Id);
        Assert.Equal(saved.Revision,migrated.Revision);
        Assert.Equal(CaptionStyle.Neutral,migrated.Edit.Style);
        Assert.Equal("Ранее сохранено",migrated.Cues[0].Text);
        var edit = migrated.Edit with {StartUs=100_000,EndUs=600_000,
            CaptionStyle=CaptionStyle.Neutral with {Position=42,Background=true}};
        var updated = library.SaveEdit(id,first.Id,migrated.Revision,edit,new(0,"Изменено",50_000,450_000));
        Assert.Equal((0L,350_000L,50_000L),(updated.Cues[0].StartUs,updated.Cues[0].EndUs,updated.Cues[0].SourceStartUs));
        Assert.Throws<UserError>(()=>library.SaveEdit(id,first.Id,updated.Revision,edit,new(0,"Пересечение",50_000,510_000)));
        var corrected = library.SaveEdit(id,first.Id,updated.Revision,edit,new(0,"Ещё правка"));
        Assert.Equal(450_000L,corrected.Cues[0].SourceEndUs);
        var reopened = new Library(Data).OpenEdit(id,first.Id);
        Assert.Equal(corrected.Revision,reopened.Revision);
        Assert.Equal(42,reopened.Edit.Style.Position);
        Assert.Equal(original.Segments,library.Open(id).Transcript!.Segments);
        var sidecar = Path.Combine(root,"Проверка O'Brien.srt");
        await library.WriteCaptionSrtAsync(id,first.Id,corrected.Revision,sidecar,default);
        string result = await File.ReadAllTextAsync(sidecar);
        Assert.Contains("Ещё правка",result);
        Assert.Contains("00:00:00,000 --> 00:00:00,350",result);
        await Assert.ThrowsAsync<UserError>(()=>library.WriteCaptionSrtAsync(id,first.Id,updated.Revision,sidecar,default));
        Assert.Equal(result,await File.ReadAllTextAsync(sidecar));
    }

    [RealMediaFact]
    public async Task BadClipScoreLeavesSavedTranscriptAndProjectAccessible()
    {
        await CreateVideo(Video, audio: true);
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, Ffmpeg, default);
        var segment = new SpeechSegment(0, 1_000_000, "Проверка сохранения.");
        var transcript = new Transcript(0, "ru", "ru", "test", 1, 1, [segment]);
        var clip = new ClipCandidate(0, 0, 0, 1_000_000, segment.Text, "Причина", new(5, 4, 4, 4));
        var selection = new ClipSelection(new("semantic", 1, 3, 1), [clip], Guid.NewGuid());
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(Data,"library.sqlite3"), Pooling=false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText="INSERT INTO transcript_cache(project_id,cache_key,selected,result_json,clips_json) VALUES ($id,'fixture',1,$transcript,$selection)";
            command.Parameters.AddWithValue("$id", project.Id.ToString("D"));
            command.Parameters.AddWithValue("$transcript", System.Text.Json.JsonSerializer.Serialize(transcript));
            command.Parameters.AddWithValue("$selection", System.Text.Json.JsonSerializer.Serialize(selection));
            command.ExecuteNonQuery();
        }
        var saved = new Library(Data).Open(project.Id);
        Assert.Equal(segment, Assert.Single(saved.Transcript!.Segments));
        Assert.Null(saved.Selection);
    }

    [RealMediaFact]
    public async Task ImportedStreamsThumbnailAndOriginalSurviveRestart()
    {
        await CreateVideo(Video, audio: true);
        var original = SHA256.HashData(await File.ReadAllBytesAsync(Video));
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, Ffmpeg, default);
        var reopened = new Library(Data);
        Assert.Equal(project, reopened.Open(project.Id));
        Assert.Equal(project, Assert.Single(reopened.List()));
        Assert.Equal(1_000_000, project.DurationUs);
        Assert.Equal((96, 64, 1), (project.Width, project.Height, project.AudioTracks));
        Assert.Equal("mpeg4", project.VideoCodec);
        Assert.Contains("mp4", project.Container);
        Assert.Equal("12/1", project.Fps);
        Assert.True(project.SourceAvailable);
        Assert.Equal(Video, project.OriginalPath);
        Assert.True(File.Exists(project.ThumbnailPath));
        Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(Video)));
    }

    [RealMediaFact]
    public async Task SilentMatroskaSupportsRelinkOnlyToMatchingBytes()
    {
        var mkv = Path.Combine(root, "без звука.mkv");
        await CreateVideo(mkv, audio: false);
        var library = new Library(Data);
        var project = await library.ImportAsync(mkv, Ffprobe!, null, default);
        Assert.Equal(0, project.AudioTracks);
        Assert.Contains("matroska", project.Container);
        var moved = Path.Combine(root, "Другая папка '2026'.mkv");
        File.Move(mkv, moved);
        Assert.False(library.Open(project.Id).SourceAvailable);
        var wrong = Path.Combine(root, "wrong.mkv");
        var changed = await File.ReadAllBytesAsync(moved);
        changed[changed.Length / 2] ^= 1;
        await File.WriteAllBytesAsync(wrong, changed);
        await Assert.ThrowsAsync<UserError>(() => library.RelinkAsync(project.Id, wrong, default));
        Assert.Equal(mkv, library.Open(project.Id).OriginalPath);
        var recovered = await library.RelinkAsync(project.Id, moved, default);
        Assert.True(recovered.SourceAvailable);
        Assert.Equal(moved, new Library(Data).Open(project.Id).OriginalPath);
    }

    [RealMediaFact]
    public async Task CorruptMediaLeavesNoProject()
    {
        await File.WriteAllTextAsync(Video, "not a video");
        var library = new Library(Data);
        await Assert.ThrowsAsync<UserError>(() => library.ImportAsync(Video, Ffprobe!, null, default));
        Assert.Empty(library.List());
    }

    [RealMediaFact]
    public async Task RebuildingProxyRequiresOriginalAndRejectsStaleSelection()
    {
        await CreateVideo(Video, audio: true);
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, null, default);
        var cached = await library.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default);
        Assert.NotNull(cached.ProxyId);
        Task<MediaProject> Rebuild() => library.CreateProxyAsync(project.Id, () => (Ffprobe!, Ffmpeg!), default, replaceProxy: cached.ProxyId);

        string moved = Path.Combine(root, "relocated.mp4");
        File.Move(Video, moved);
        Assert.Equal("source_unavailable", (await Assert.ThrowsAsync<UserError>(Rebuild)).Code);
        File.Copy(moved, Video);
        var changed = await File.ReadAllBytesAsync(Video);
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(Video, changed);
        File.SetLastWriteTimeUtc(Video, File.GetLastWriteTimeUtc(moved));
        Assert.Equal("source_mismatch", (await Assert.ThrowsAsync<UserError>(Rebuild)).Code);
        Assert.Equal(cached.ProxyId, library.Open(project.Id).ProxyId);
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(cached.ProxyPath!)!));

        await library.RelinkAsync(project.Id, moved, default);
        var repaired = await Rebuild();
        Assert.NotEqual(cached.ProxyId, repaired.ProxyId);
        Assert.Equal("proxy_changed", (await Assert.ThrowsAsync<UserError>(Rebuild)).Code);
        Assert.Equal(repaired.ProxyId, new Library(Data).Open(project.Id).ProxyId);
    }

    [RealMediaFact]
    public async Task ProxyForSilentVideoStaysSeparateFromOriginal()
    {
        var silent = Path.Combine(root, "источник.mkv");
        await CreateVideo(silent, audio: false);
        var library = new Library(Data);
        var project = await library.ImportAsync(silent, Ffprobe!, null, default);
        var original = SHA256.HashData(await File.ReadAllBytesAsync(silent));
        var ready = await library.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default);
        Assert.NotNull(ready.ProxyPath);
        var media = await MediaProbe.ReadAsync(Ffprobe!, ready.ProxyPath, default);
        Assert.Equal(("h264", 0, 1_000_000), (media.VideoCodec, media.AudioTracks, media.DurationUs));
        Assert.Equal(silent, ready.OriginalPath);
        Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(silent)));
        Assert.Equal(ready.ProxyPath, new Library(Data).Open(project.Id).ProxyPath);
    }

    [RealMediaFact]
    public async Task VersionThreeProjectCanMeasureAndReadFrameIntervalsOffline()
    {
        await CreateVideo(Video, audio: true);
        var project = await new Library(Data).ImportAsync(Video, Ffprobe!, null, default);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE export_job; DROP TABLE edit_text; DROP TABLE edit_clip; DROP TABLE transcript_cache; DROP TABLE frame_sample; PRAGMA user_version=3;";
            cmd.ExecuteNonQuery();
        }
        var library = new Library(Data);
        Assert.Null(library.Open(project.Id).FrameIntervals);
        var measured = (await library.MeasureFrameIntervalsAsync(project.Id, Ffprobe!, default)).FrameIntervals!;
        Assert.Equal(12, measured.Frames);
        Assert.Equal(1000d / 12, measured.MinMs, 6);
        Assert.Equal(measured.MinMs, measured.MaxMs);
        File.Move(Video, Path.Combine(root, "moved.mp4"));
        Assert.Equal(measured, new Library(Data).Open(project.Id).FrameIntervals);
    }

    [Theory]
    [InlineData("pts=0")]
    [InlineData("pts=7\npts=7")]
    [InlineData("pts=1\npts=N/A\npts=4")]
    [InlineData("pts=15\npts=11")]
    public void UnusableFrameTimestampsDoNotYieldIntervals(string frames)
    {
        Assert.Equal("frame_timing_unknown", Assert.Throws<UserError>(() => Library.ReadFrameIntervals(frames + "\ntime_base=1/1000")).Code);
    }

    [RealMediaFact]
    public async Task VersionOneLibraryPreservesItsOriginalThroughMigration()
    {
        await CreateVideo(Video, audio: true);
        var project = await new Library(Data).ImportAsync(Video, Ffprobe!, null, default);
        // v1 has all imported metadata and the original fingerprint, but no proxy_id.
        var connectionString = new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE export_job; DROP TABLE edit_text; DROP TABLE edit_clip; DROP TABLE transcript_cache; DROP TABLE frame_sample; DROP TABLE proxy_track; ALTER TABLE media_asset DROP COLUMN proxy_id; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }
        var migrated = new Library(Data);
        Assert.Equal(project, Assert.Single(migrated.List()));
        using var original = await migrated.OpenOriginalForProcessingAsync(project.Id, default);
        Assert.Equal(Video, original.Name);
        var ready = await migrated.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default);
        Assert.NotNull(ready.ProxyPath);
        Assert.Equal(ready, new Library(Data).Open(project.Id));

        using var inspector = new SqliteConnection(connectionString);
        inspector.Open();
        using var inspect = inspector.CreateCommand();
        inspect.CommandText = "PRAGMA user_version";
        Assert.Equal(9L, inspect.ExecuteScalar());
        inspect.CommandText = "PRAGMA user_version = 10";
        inspect.ExecuteNonQuery();
        Assert.Equal("database_newer", Assert.Throws<UserError>(() => new Library(Data)).Code);
        inspect.CommandText = "PRAGMA user_version";
        Assert.Equal(10L, inspect.ExecuteScalar());
        inspect.CommandText = "SELECT original_path FROM media_asset WHERE project_id=$id";
        inspect.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        Assert.Equal(Video, inspect.ExecuteScalar());
    }

    [RealMediaFact]
    public async Task VersionTwoProxyRemainsPlayableAsFirstTrack()
    {
        await CreateVideo(Video, audio: true);
        var original = new Library(Data);
        var imported = await original.ImportAsync(Video, Ffprobe!, null, default);
        var oldProxy = await original.CreateProxyAsync(imported.Id, Ffprobe!, Ffmpeg!, default);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString()))
        {
            db.Open();
            using var setup = db.CreateCommand();
            setup.CommandText = "DROP TABLE export_job; DROP TABLE edit_text; DROP TABLE edit_clip; DROP TABLE transcript_cache; DROP TABLE frame_sample; DROP TABLE proxy_track; PRAGMA user_version=2;";
            setup.ExecuteNonQuery();
        }
        var library = new Library(Data);
        var cached = await library.CreateProxyAsync(imported.Id, Ffprobe!, "unused.exe", default, audioTrack: 0);
        Assert.Equal(oldProxy.ProxyPath, cached.ProxyPath);
        var error = await Assert.ThrowsAsync<UserError>(() =>
            library.CreateProxyAsync(imported.Id, Ffprobe!, Ffmpeg!, default, audioTrack: 1));
        Assert.Equal("audio_track", error.Code);
        Assert.Equal(cached, library.Open(imported.Id));
    }

    [RealMediaFact]
    public async Task RenderReadsFullResolutionOriginalAfterProxyAndRestart()
    {
        await CreateVideo(Video, audio: false, size: "1440x810");
        var fingerprint = SHA256.HashData(await File.ReadAllBytesAsync(Video));
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, null, default);
        var ready = await library.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default);
        var preview = await MediaProbe.ReadAsync(Ffprobe!, ready.ProxyPath!, default);
        Assert.Equal((1280, 720), (preview.Width, preview.Height));

        var restarted = new Library(Data);
        var output = Path.Combine(root, "контроль рендера.mkv");
        using (var source = await restarted.OpenOriginalForProcessingAsync(project.Id, default))
        {
            await MediaProcess.RunAsync(Ffmpeg!, ["-nostdin", "-v", "error", "-n", "-i", source.Name,
                "-t", "0.5", "-map", "0:v:0", "-c:v", "ffv1", "-threads", "1", output],
                TimeSpan.FromSeconds(30), 4096, default);
        }
        var render = await MediaProbe.ReadAsync(Ffprobe!, output, default);
        Assert.Equal((1440, 810, "ffv1", 500_000L), (render.Width, render.Height, render.VideoCodec, render.DurationUs));
        Assert.Equal(fingerprint, SHA256.HashData(await File.ReadAllBytesAsync(Video)));
    }

    [RealMediaFact]
    public async Task ProcessingRefusesMissingOrSubstitutedOriginalEvenWithPlayableProxy()
    {
        await CreateVideo(Video, audio: true);
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, null, default);
        var ready = await library.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default);
        var moved = Path.Combine(root, "перенесённый оригинал.mp4");
        File.Move(Video, moved);
        var restarted = new Library(Data);
        Assert.Equal("source_unavailable", (await Assert.ThrowsAsync<UserError>(() =>
            restarted.OpenOriginalForProcessingAsync(project.Id, default))).Code);
        var changed = await File.ReadAllBytesAsync(moved);
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(Video, changed);
        File.SetLastWriteTimeUtc(Video, File.GetLastWriteTimeUtc(moved));
        Assert.True(restarted.Open(project.Id).SourceAvailable);
        Assert.Equal("source_mismatch", (await Assert.ThrowsAsync<UserError>(() =>
            restarted.OpenOriginalForProcessingAsync(project.Id, default))).Code);
        await restarted.RelinkAsync(project.Id, moved, default);
        using var verified = await new Library(Data).OpenOriginalForProcessingAsync(project.Id, default);
        Assert.Equal(moved, verified.Name);
        Assert.Equal(0, verified.Position);
        Assert.Equal(ready.ProxyPath, restarted.Open(project.Id).ProxyPath);
    }

    [RealMediaFact]
    public async Task LegacyProxySurvivesFailedRefreshAndMissingOriginal()
    {
        await CreateVideo(Video, audio: false);
        var library = new Library(Data);
        var imported = await library.ImportAsync(Video, Ffprobe!, null, default);
        var created = await library.CreateProxyAsync(imported.Id, Ffprobe!, Ffmpeg!, default);
        var current = Assert.IsType<string>(created.ProxyPath);
        var legacy = Path.ChangeExtension(Path.ChangeExtension(current, null), ".mp4");
        File.Move(current, legacy);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(legacy));

        var reopened = new Library(Data);
        Assert.True(reopened.Open(imported.Id).ProxyNeedsRefresh);
        await Assert.ThrowsAsync<UserError>(() => reopened.CreateProxyAsync(imported.Id, Ffprobe!, "missing-encoder.exe", default));
        File.Move(Video, Path.Combine(root, "перенесённый.mp4"));
        var project = new Library(Data).Open(imported.Id);
        Assert.False(project.SourceAvailable);
        Assert.True(project.ProxyNeedsRefresh);
        Assert.Equal(legacy, project.ProxyPath);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(legacy)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(current)!, "*.partial.mp4"));
    }

    [RealMediaFact]
    public async Task AudioProxyKeepsAudioAndIsReusedAfterRestart()
    {
        await CreateVideo(Video, audio: true);
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, null, default);
        var ready = await library.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default);
        Assert.NotNull(ready.ProxyPath);
        var media = await MediaProbe.ReadAsync(Ffprobe!, ready.ProxyPath, default);
        Assert.Equal(("h264", 1), (media.VideoCodec, media.AudioTracks));
        Assert.Contains("\"aac\"", media.Json);
        Assert.InRange(media.DurationUs, 950_000, 1_100_000);
        var reopened = new Library(Data);
        Assert.Equal(ready.ProxyPath, (await reopened.CreateProxyAsync(project.Id, Ffprobe!, "missing-encoder.exe", default)).ProxyPath);
    }

    [RealMediaFact]
    public async Task PqOriginalCreatesPersistentBt709PreviewOnRequest()
    {
        await CreateVideo(Video, audio: false, hdrMetadata: true);
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, Ffmpeg, default);
        Assert.Equal("smpte2084", project.Color?.Transfer);
        Assert.Null(project.ThumbnailPath);
        Assert.Null(project.ProxyPath);
        var preview = await library.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default);
        Assert.Equal(preview.ProxyPath, new Library(Data).Open(project.Id).ProxyPath);
        var probe = await MediaProbe.ReadAsync(Ffprobe!, preview.ProxyPath!, default);
        Assert.Contains("\"color_transfer\": \"bt709\"", probe.Json);
        Assert.False(probe.IsHdr);
    }

    [RealMediaFact]
    public async Task DamagedProbeSnapshotDoesNotLoseProjectOrBypassLiveHdrCheck()
    {
        await CreateVideo(Video, audio: false, hdrMetadata: true, colorMatrix: "unknown");
        var library = new Library(Data);
        var project = await library.ImportAsync(Video, Ffprobe!, Ffmpeg, default);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(Data, "library.sqlite3"), Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE media_asset SET streams_json='{' WHERE project_id=$id";
            command.Parameters.AddWithValue("$id", project.Id.ToString("D"));
            command.ExecuteNonQuery();
        }
        var reopened = new Library(Data).Open(project.Id);
        Assert.True(reopened.SourceAvailable);
        Assert.Equal(Video, reopened.OriginalPath);
        Assert.Null(reopened.Color);
        var error = await Assert.ThrowsAsync<UserError>(() => library.CreateProxyAsync(project.Id, Ffprobe!, Ffmpeg!, default));
        Assert.Equal("proxy_hdr", error.Code);
        Assert.Null(library.Open(project.Id).ProxyPath);
    }

    [RealMediaFact]
    public async Task ReferencedVideoInConcatPlaylistIsRejected()
    {
        var video = Path.Combine(root, "clip.mp4");
        await CreateVideo(video, audio: false);
        var playlist = Path.Combine(root, "playlist.ffconcat");
        await File.WriteAllTextAsync(playlist, "ffconcat version 1.0\nfile 'clip.mp4'\n");
        var library = new Library(Data);
        await Assert.ThrowsAsync<UserError>(() => library.ImportAsync(playlist, Ffprobe!, Ffmpeg, default));
        Assert.Empty(library.List());
    }

    [Fact]
    public async Task MissingProbeAndCancellationLeaveNoProject()
    {
        await File.WriteAllTextAsync(Video, "unreadable");
        var library = new Library(Data);
        var missing = Path.Combine(root, "missing-ffprobe.exe");
        var error = await Assert.ThrowsAsync<UserError>(() => library.ImportAsync(Video, missing, null, default));
        Assert.DoesNotContain(root, error.Message);
        Assert.Null(await Library.ToolVersionAsync(missing, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            library.ImportAsync(Video, missing, null, new CancellationToken(true)));
        Assert.Empty(new Library(Data).List());
    }

    private static async Task CreateVideo(string path, bool audio, bool hdrMetadata = false, string size = "96x64", string colorMatrix = "bt2020nc")
    {
        var start = new ProcessStartInfo(Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true };
        string[] args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", $"color=c=red:size={size}:rate=12"];
        foreach (var value in args) start.ArgumentList.Add(value);
        if (audio)
            foreach (var value in new[] { "-f", "lavfi", "-i", "sine=frequency=330", "-c:a", "aac" })
                start.ArgumentList.Add(value);
        if (hdrMetadata)
            foreach (var value in new[] { "-x264-params", $"colorprim=bt2020:transfer=smpte2084:colormatrix={colorMatrix}" })
                start.ArgumentList.Add(value);
        foreach (var value in new[] { "-c:v", hdrMetadata ? "libx264" : "mpeg4", "-t", "1", path }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.Equal(0, process.ExitCode);
    }

    [RealMediaFact]
    public async Task InvalidAndStaleEditsRollBackWithoutChangingOtherClips()
    {
        await CreateVideo(Video, audio: false);
        var db = new Library(Data);
        var project = await db.ImportAsync(Video, Ffprobe!, null, default);
        var first = db.CreateEdit(project.Id);
        var other = db.CreateEdit(project.Id);
        EditorClip Save(int revision, EditDecision decision, CueChange? cue = null) =>
            db.SaveEdit(project.Id, first.Id, revision, decision, cue);
        foreach (var bad in new[] { first.Edit with { EndUs=project.DurationUs+1 }, first.Edit with { EndUs=0 },
            first.Edit with { StartUs=-1 }, first.Edit with { X=double.NaN }, first.Edit with { Y=1.01 }, first.Edit with { Frame="auto" } })
            Assert.Throws<UserError>(()=>Save(1,bad));
        Assert.Throws<UserError>(()=>Save(1,first.Edit,new CueChange(99,"Missing")));
        var good = Save(1,first.Edit with { Title="Сохранено", Frame="fit", X=.75 });
        Assert.Equal(2,good.Revision);
        Assert.Throws<UserError>(()=>Save(1,first.Edit));
        var reopened = new Library(Data);
        Assert.Equal(good.Edit,reopened.OpenEdit(project.Id,first.Id).Edit);
        Assert.Equal(other.Edit,reopened.OpenEdit(project.Id,other.Id).Edit);
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder
            {DataSource=Path.Combine(Data,"library.sqlite3"),Pooling=false}.ToString());
        connection.Open();
        using var damage=connection.CreateCommand();
        damage.CommandText="UPDATE edit_clip SET decision='{' WHERE id=$id";
        damage.Parameters.AddWithValue("$id",other.Id.ToString("D"));
        damage.ExecuteNonQuery();
        Assert.Equal(2,reopened.Open(project.Id).Edits.Length);
        Assert.Throws<UserError>(()=>reopened.OpenEdit(project.Id,other.Id));
        Assert.Equal(good.Edit,reopened.OpenEdit(project.Id,first.Id).Edit);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public sealed class RealMediaFactAttribute : FactAttribute
{
    public RealMediaFactAttribute()
    {
        if (LibraryTests.Ffmpeg is null || LibraryTests.Ffprobe is null)
            Skip = "Set SHORTFORGE_FFMPEG and SHORTFORGE_FFPROBE to installed executables or add them to PATH.";
    }
}
