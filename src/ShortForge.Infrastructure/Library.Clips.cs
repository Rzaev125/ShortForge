using System.Text.Json;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed partial class Library
{
    internal static ClipSelection? DecodeSelection(string json, SpeechSegment[] segments)
    {
        if (json.Length > 64_000) return null;
        try
        {
            var result = JsonSerializer.Deserialize<ClipSelection>(json);
            if (result?.Search is not { IsValid: true } search || result.Clips == null || result.Clips.Length > search.Count) return null;
            foreach (var c in result.Clips)
            {
                if (c == null || c.StartId < 0 || c.EndId < c.StartId || c.EndId >= segments.Length ||
                    segments[c.StartId].StartUs != c.StartUs || segments[c.EndId].EndUs != c.EndUs || !search.Fits(c.EndUs - c.StartUs) ||
                    string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Reason) || c.State is not ("offered" or "selected" or "rejected") ||
                    (search.Mode == "semantic") != (c.Scores != null) ||
                    c.Scores is { Independence: < 0 or > 4 } or { Clarity: < 0 or > 4 } or { Opening: < 0 or > 4 } or { Completion: < 0 or > 4 }) return null;
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    public MediaProject MarkClip(Guid id, Guid selectionId, int startId, string state)
    {
        if (state is not ("offered" or "selected" or "rejected")) throw new UserError("clip_state", "Неизвестное состояние фрагмента.");
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT clips_json FROM transcript_cache WHERE project_id=$id AND selected=1";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        string? json = cmd.ExecuteScalar() as string;
        ClipSelection? selection = null;
        if (json is { Length: <= 64000 })
        {
            try { selection = JsonSerializer.Deserialize<ClipSelection>(json); }
            catch (JsonException) { throw new UserError("clip_changed","Подборка повреждена. Запустите подбор снова."); }
        }
        int index = selection?.Clips == null ? -1 : Array.FindIndex(selection.Clips, c => c?.StartId == startId);
        if (selection == null || selection.Id != selectionId || index < 0)
            throw new UserError("clip_changed", "Подборка изменилась. Откройте проект снова.");
        selection.Clips[index] = selection.Clips[index] with { State = state };
        cmd.CommandText = "UPDATE transcript_cache SET clips_json=$json WHERE project_id=$id AND selected=1";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(selection));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return Open(id);
    }

    public async Task<MediaProject> FindClipsAsync(Guid id, ClipSearch search, CancellationToken token, IProgress<ImportProgress>? progress = null)
    {
        if (!search.IsValid) throw new UserError("clip_params", "Проверьте длительность и число фрагментов.");
        var media = ReadProjects(id).SingleOrDefault() ?? throw MissingProject();
        string key;
        SpeechSegment[] speech;
        using (var db = Connect())
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT cache_key,result_json FROM transcript_cache WHERE project_id=$id AND selected=1";
            read.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = read.ExecuteReader();
            if (!reader.Read() || DecodeCached(reader.GetString(1), media)?.Segments is not { Length: > 0 } segments)
                throw new UserError("clip_speech", "Для подбора нужна расшифровка с речью.");
            key = reader.GetString(0);
            speech = segments;
        }
        progress?.Report(new(search.Mode == "semantic" ? "clipSemantic" : "clipStructure"));
        var clips = search.Mode == "structure" ? CandidatePicker.ByStructure(speech, search, token) :
            await LocalSemantic.FindAsync(speech, search, token, progress);
        using var connection = Connect();
        using var tx = connection.BeginTransaction();
        using var save = connection.CreateCommand();
        save.Transaction = tx;
        save.CommandText = "UPDATE transcript_cache SET clips_json=$clips WHERE project_id=$id AND cache_key=$key AND selected=1";
        save.Parameters.AddWithValue("$id", id.ToString("D"));
        save.Parameters.AddWithValue("$key", key);
        save.Parameters.AddWithValue("$clips", JsonSerializer.Serialize(new ClipSelection(search, clips, Guid.NewGuid())));
        if (save.ExecuteNonQuery() != 1) throw new UserError("clip_changed", "Расшифровка изменилась. Запустите подбор ещё раз.");
        save.CommandText = "UPDATE project SET updated_utc=$now WHERE id=$id";
        save.Parameters.AddWithValue("$now", UtcNow());
        save.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return Open(id);
    }
}
