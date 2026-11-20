using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record SpeechSegment(long StartUs, long EndUs, string Text);
public sealed record Transcript(int AudioTrack, string Language, string DetectedLanguage, string Model,
    long ElapsedMs, long PeakRamBytes, SpeechSegment[] Segments);

public sealed partial class Library
{
    private const string AsrModel = "whisper.cpp 1.9.2 · small · Silero 6.2.0 · CPU";
    private const string SmallHash = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b";
    private const string VadHash = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";

    private MediaProject ReadTranscript(MediaProject project)
    {
        using var db = Connect();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT result_json,clips_json FROM transcript_cache WHERE project_id=$id AND selected=1";
        query.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return project;
        var transcript = DecodeCached(reader.GetString(0), project);
        return project with { Transcript = transcript, Selection = transcript == null || reader.IsDBNull(1) ? null :
            DecodeSelection(reader.GetString(1), transcript.Segments) };
    }

    private static Transcript? DecodeCached(string json, MediaProject project)
    {
        if (json.Length > 16_000_000) return null;
        try
        {
            var result = JsonSerializer.Deserialize<Transcript>(json);
            if (result is not { Segments: not null, ElapsedMs: >= 0, PeakRamBytes: >= 0 } ||
                result.AudioTrack < 0 || result.AudioTrack >= project.AudioTracks || string.IsNullOrWhiteSpace(result.Model) ||
                string.IsNullOrWhiteSpace(result.DetectedLanguage) || result.Language is not ("auto" or "en" or "ru" or "az")) return null;
            long previous = 0;
            foreach (var s in result.Segments)
            {
                if (s == null || s.StartUs < previous || s.EndUs <= s.StartUs || s.EndUs > project.DurationUs ||
                    string.IsNullOrWhiteSpace(s.Text)) return null;
                previous = s.EndUs;
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    private bool SelectTranscript(MediaProject project, string key, Transcript? completed, CancellationToken token)
    {
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        query.Parameters.AddWithValue("$key", key);
        if (completed == null)
        {
            query.CommandText = "SELECT result_json,selected FROM transcript_cache WHERE project_id=$id AND cache_key=$key";
            using var reader = query.ExecuteReader();
            if (!reader.Read() || DecodeCached(reader.GetString(0), project) == null) return false;
            if (reader.GetBoolean(1))
            {
                token.ThrowIfCancellationRequested();
                return true;
            }
        }
        query.CommandText = "UPDATE transcript_cache SET selected=0 WHERE project_id=$id AND selected=1";
        query.ExecuteNonQuery();
        if (completed != null)
        {
            query.CommandText = """
                INSERT INTO transcript_cache(project_id,cache_key,selected,result_json) VALUES ($id,$key,1,$json)
                ON CONFLICT(project_id,cache_key) DO UPDATE SET selected=1,result_json=excluded.result_json,clips_json=NULL
                """;
            query.Parameters.AddWithValue("$json", JsonSerializer.Serialize(completed));
        }
        else query.CommandText = "UPDATE transcript_cache SET selected=1 WHERE project_id=$id AND cache_key=$key";
        query.ExecuteNonQuery();
        query.CommandText = "UPDATE project SET updated_utc=$now WHERE id=$id";
        query.Parameters.AddWithValue("$now", UtcNow());
        query.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return true;
    }

    public async Task<MediaProject> TranscribeAsync(Guid id, int track, string language, string? probe, string? ffmpeg,
        CancellationToken token, IProgress<ImportProgress>? progress = null)
    {
        var project = Open(id);
        if (project.AudioTracks == 0) throw new UserError("no_audio", "В видео нет аудиодорожки.");
        if (track < 0 || track >= project.AudioTracks || language is not ("auto" or "ru" or "en" or "az"))
            throw new UserError("asr_params", "Проверьте выбранный язык и дорожку.");
        progress?.Report(new("fingerprint"));
        using var source = await OpenOriginalForProcessingAsync(id, token);
        string key = $"{ReadOriginal(id).Hash}/{track}/{language}/{AsrModel}/pcm16k-vad-segments-v1";
        if (SelectTranscript(project, key, null, token)) return Open(id);
        if (probe == null || ffmpeg == null)
            throw new UserError("missing_ffmpeg", "Для извлечения звука нужны ffmpeg.exe и ffprobe.exe.");
        progress?.Report(new("asrTools"));
        var cli = AsrFile("tools/whisper-v1.9.2/Release/whisper-cli.exe");
        var model = AsrFile("models/ggml-small.bin");
        var vad = AsrFile("models/ggml-silero-v6.2.0.bin");
        using var cliLock = await VerifyAsrFile(cli, "95e3c0b0e778ad9499eb0125f97c1dcf437dd9eb4ea77050b043574f93c2631d", token);
        using var modelLock = await VerifyAsrFile(model, SmallHash, token);
        using var vadLock = await VerifyAsrFile(vad, VadHash, token);
        var work = Directory.CreateDirectory(Path.Combine(asrWorkDirectory, Guid.NewGuid().ToString("N"))).FullName;
        var audio = Path.Combine(work, "audio.wav");
        var prefix = Path.Combine(work, "result");
        var jsonPath = prefix + ".json";
        try
        {
            progress?.Report(new("asrAudio"));
            await ExtractAudioAsync(source.Name, track, audio, ffmpeg, probe, token);
            progress?.Report(new("asrListen"));
            long peak = 0;
            var timer = Stopwatch.StartNew();
            await MediaProcess.RunAsync(cli, ["--model", model, "--file", "audio.wav", "--language", language, "--threads", "4",
                "--no-gpu", "--vad", "--vad-model", vad, "--suppress-nst", "--no-speech-thold", "0.60",
                "--no-prints", "--output-json", "--output-file", "result"], Timeout.InfiniteTimeSpan, 0, token,
                observePeakBytes: bytes => peak = Math.Max(peak, bytes), workingDirectory: work);
            timer.Stop();
            var output = new FileInfo(jsonPath);
            if (!output.Exists || output.Length is 0 or > 12_000_000) throw InvalidTranscript();
            var (detected, segments) = ParseSegments(await File.ReadAllTextAsync(jsonPath, token), project.DurationUs);
            var result = new Transcript(track, language, detected, AsrModel, timer.ElapsedMilliseconds, peak, segments);
            SelectTranscript(project, key, result, token);
            return Open(id);
        }
        catch (UserError ex) when (ex.Code == "media_unreadable")
        { throw new UserError("asr_failed", "Не удалось обработать выбранную дорожку. Проверьте исходник, локальные инструменты и доступную память."); }
        finally
        {
            File.Delete(jsonPath);
            File.Delete(audio);
            Directory.Delete(work);
        }
    }

    private static string AsrFile(string relative)
    {
        var configured = Environment.GetEnvironmentVariable("SHORTFORGE_ASR_HOME");
        if (!string.IsNullOrEmpty(configured))
        {
            if (Path.IsPathFullyQualified(configured) && Directory.Exists(configured))
                return Path.Combine(configured, relative);
        }
        else
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, relative);
            if (File.Exists(bundled)) return bundled;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ShortForge.slnx")))
                    return Path.Combine(dir.FullName, relative.Replace("tools/", "tools/bin/", StringComparison.Ordinal));
        }
        throw new UserError("asr_missing", "Укажите SHORTFORGE_ASR_HOME с папками tools и models по инструкции README.");
    }

    private static async Task<FileStream> VerifyAsrFile(string path, string expected, CancellationToken token)
    {
        if (!File.Exists(path)) throw new UserError("asr_missing", $"Для распознавания нужен {Path.GetFileName(path)}. Размещение файлов указано в README.");
        var file = File.OpenRead(path);
        try
        {
            var hash = await SHA256.HashDataAsync(file, token);
            if (!Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new UserError("asr_version", $"Контрольная сумма {Path.GetFileName(path)} отличается от версии в README.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static async Task ExtractAudioAsync(string source, int track, string destination, string ffmpeg, string probe, CancellationToken token)
    {
        // Retain leading gaps and stream discontinuities on the player's normalized media timeline.
        await MediaProcess.RunAsync(ffmpeg, ["-v","error","-nostdin","-n","-xerror","-protocol_whitelist","file",
            "-format_whitelist",MediaProbe.StandaloneFormats,"-i",source,"-map","0:a:" + track.ToString(CultureInfo.InvariantCulture),
            "-vn","-map_metadata","-1","-af","aresample=16000:async=1:first_pts=0","-ac","1","-c:a","pcm_s16le","-f","wav",destination],
            Timeout.InfiniteTimeSpan, 0, token);
        var report = await MediaProcess.RunAsync(probe, ["-v","error","-show_entries","stream=codec_name,channels,sample_rate:format=duration",
            "-of","json",destination], TimeSpan.FromSeconds(30), 8192, token);
        using var doc = JsonDocument.Parse(report);
        var tracks = doc.RootElement.GetProperty("streams");
        if (tracks.GetArrayLength() != 1 || tracks[0].GetProperty("codec_name").GetString() != "pcm_s16le" ||
            tracks[0].GetProperty("channels").GetInt32() != 1 || tracks[0].GetProperty("sample_rate").GetString() != "16000" ||
            !decimal.TryParse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var duration) || duration <= 0)
            throw new UserError("asr_audio", "Не удалось получить монофонический PCM WAV 16 кГц.");
    }

    private static UserError InvalidTranscript() => new("asr_output", "whisper.cpp вернул повреждённую расшифровку или недопустимые временные метки.");

    internal static (string Language, SpeechSegment[] Segments) ParseSegments(string json, long durationUs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("model").GetProperty("type").GetString() != "small" ||
                !root.GetProperty("model").GetProperty("multilingual").GetBoolean()) throw InvalidTranscript();
            var detected = root.GetProperty("result").GetProperty("language").GetString();
            if (string.IsNullOrWhiteSpace(detected)) throw InvalidTranscript();
            List<SpeechSegment> segments = [];
            long previousEnd = 0;
            foreach (var entry in root.GetProperty("transcription").EnumerateArray())
            {
                var offsets = entry.GetProperty("offsets");
                long start = checked(offsets.GetProperty("from").GetInt64() * 1000);
                long end = checked(offsets.GetProperty("to").GetInt64() * 1000);
                if (end - (decimal)durationUs > 250_000) throw InvalidTranscript();
                end = Math.Min(durationUs, end);
                var text = entry.GetProperty("text").GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (segments.Count > 0 && segments[^1] == new SpeechSegment(start, end, text)) continue;
                if (start < previousEnd || end <= start || text.Length > 16000) throw InvalidTranscript();
                segments.Add(new(start, end, text));
                previousEnd = end;
            }
            return (detected, segments.ToArray());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw InvalidTranscript(); }
    }
}
