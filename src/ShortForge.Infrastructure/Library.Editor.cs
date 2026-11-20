using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record EditSummary(Guid Id, string Title);
public sealed record EditorCue(int Index, long StartUs, long EndUs, string Text, long SourceStartUs, long SourceEndUs);
public sealed record EditorClip(Guid Id, int Revision, int AudioTrack, EditDecision Edit, EditorCue[] Cues);

public sealed partial class Library
{
    private static UserError InvalidEdit() => new("invalid_edit", "Проверьте границы клипа, название, кадр и текст.");
    private static UserError ChangedEdit() => new("edit_changed", "Клип изменился. Закройте редактор и откройте клип снова.");

    private EditSummary[] ListEdits(Guid projectId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, CASE WHEN json_valid(decision) THEN json_extract(decision,'$.Title') END
            FROM edit_clip WHERE project_id=$project ORDER BY rowid
            """;
        cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
        using var rows = cmd.ExecuteReader();
        var edits = new List<EditSummary>();
        while (rows.Read()) edits.Add(new(Guid.Parse(rows.GetString(0)), rows.IsDBNull(1) ? "Повреждённый клип" : rows.GetString(1)));
        return edits.Count == 0 ? [] : edits.ToArray();
    }

    public EditorClip CreateEdit(Guid projectId, Guid? selectionId = null, int? startId = null)
    {
        var project = ReadProjects(projectId).SingleOrDefault() ?? throw MissingProject();
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
        cmd.Parameters.AddWithValue("$selection", (object?)selectionId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", (object?)startId ?? DBNull.Value);
        cmd.CommandText = "SELECT id FROM edit_clip WHERE project_id=$project AND selection_id=$selection AND start_id=$start";
        if (cmd.ExecuteScalar() is string existing)
            return ReadEdit(cmd, project, Guid.Parse(existing));
        string? key = null;
        var track = project.ProxyAudioTrack;
        ClipCandidate? candidate = null;
        cmd.CommandText = "SELECT cache_key,result_json,clips_json FROM transcript_cache WHERE project_id=$project AND selected=1";
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read() && DecodeCached(r.GetString(1), project) is {} transcript)
            {
                key = r.GetString(0);
                track = transcript.AudioTrack;
                if (selectionId.HasValue && !r.IsDBNull(2) && DecodeSelection(r.GetString(2), transcript.Segments) is {} selection &&
                    selection.Id == selectionId)
                    candidate = selection.Clips.SingleOrDefault(c => c.StartId == startId);
            }
        }
        if (selectionId.HasValue && candidate == null) throw ChangedEdit();
        var title = candidate?.Title ?? "Новый клип";
        var edit = new EditDecision(title[..Math.Min(240, title.Length)], candidate?.StartUs ?? 0,
            candidate?.EndUs ?? Math.Min(project.DurationUs, 30_000_000), "crop", .5, .5);
        if (!edit.Fits(project.DurationUs)) throw InvalidEdit();
        var id = Guid.NewGuid();
        cmd.CommandText = """
            INSERT INTO edit_clip(id, project_id, selection_id, start_id, transcript_key, audio_track, revision, decision)
            VALUES($id,$project,$selection,$start,$key,$track,1,$edit);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$track", track);
        cmd.Parameters.AddWithValue("$edit", JsonSerializer.Serialize(edit));
        cmd.ExecuteNonQuery();
        TouchEditorProject(cmd);
        var result = ReadEdit(cmd, project, id);
        tx.Commit();
        return result;
    }

    private static void TouchEditorProject(SqliteCommand cmd)
    {
        cmd.CommandText = "UPDATE project SET updated_utc=$now WHERE id=$project";
        cmd.Parameters.AddWithValue("$now", UtcNow());
        cmd.ExecuteNonQuery();
    }

    public EditorClip OpenEdit(Guid projectId, Guid id)
    {
        var project = ReadProjects(projectId).SingleOrDefault() ?? throw MissingProject();
        using var db = Connect();
        using var tx = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        return ReadEdit(cmd, project, id);
    }

    private static EditorClip ReadEdit(SqliteCommand cmd, MediaProject project, Guid id)
    {
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$project", project.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.CommandText = """
            SELECT e.revision,e.audio_track,e.decision,t.result_json,e.transcript_key
            FROM edit_clip e LEFT JOIN transcript_cache t ON t.project_id=e.project_id AND t.cache_key=e.transcript_key
            WHERE e.id=$id AND e.project_id=$project
            """;
        int revision, track;
        EditDecision edit;
        SpeechSegment[] segments = [];
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw ChangedEdit();
            revision = r.GetInt32(0);
            track = r.GetInt32(1);
            try { edit = JsonSerializer.Deserialize<EditDecision>(r.GetString(2)) ?? throw InvalidEdit(); }
            catch (JsonException) { throw new UserError("damaged_edit", "Не удалось прочитать сохранённый монтаж этого клипа."); }
            if (!edit.Fits(project.DurationUs) || edit.Frame == "auto" && edit.Trajectory == null ||
                edit.Trajectory != null && !edit.Trajectory.Fits(edit.EndUs - edit.StartUs)) throw InvalidEdit();
            if (!r.IsDBNull(4))
                segments = !r.IsDBNull(3) && DecodeCached(r.GetString(3), project) is {} transcript
                    ? transcript.Segments : throw new UserError("edit_transcript", "Исходная расшифровка клипа недоступна.");
        }
        var overrides = new Dictionary<int,CueChange>();
        cmd.CommandText = "SELECT segment_index,body,source_start_us,source_end_us FROM edit_text WHERE clip_id=$id";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                int index = r.GetInt32(0);
                overrides.Add(index, new(index, r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3)));
            }
        long previousEnd = 0;
        var cues = new List<EditorCue>();
        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var change = overrides.GetValueOrDefault(i);
            long start = change?.SourceStartUs ?? segment.StartUs;
            long end = change?.SourceEndUs ?? segment.EndUs;
            if (start < previousEnd || end <= start || end > project.DurationUs)
                throw new UserError("cue_range", "Границы фразы должны находиться в видео и не пересекать соседние фразы.");
            previousEnd = end;
            if (edit.ClipTime(start, end) is {} range)
                cues.Add(new(i, range.StartUs, range.EndUs, change?.Text ?? segment.Text, start, end));
        }
        return new(id, revision, track, edit, cues.ToArray());
    }

    public EditorClip SaveEdit(Guid projectId, Guid id, int revision, EditDecision edit, CueChange? cue)
        => WriteEdit(projectId, id, revision, edit, cue, null, default);

    private EditorClip WriteEdit(Guid projectId, Guid id, int revision, EditDecision edit, CueChange? cue,
        CropTrajectory? generated, CancellationToken token)
    {
        var project = ReadProjects(projectId).SingleOrDefault() ?? throw MissingProject();
        if (!edit.Fits(project.DurationUs)) throw InvalidEdit();
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        var previous = ReadEdit(cmd, project, id);
        if (previous.Revision != revision) throw ChangedEdit();
        bool trimmed = edit.StartUs != previous.Edit.StartUs || edit.EndUs != previous.Edit.EndUs;
        var trajectory = trimmed ? null : generated ?? previous.Edit.Trajectory;
        edit = edit with { Trajectory = trajectory, Frame = trimmed && edit.Frame == "auto" ? "fit" : edit.Frame };
        if (edit.Frame == "auto" && trajectory == null)
            throw new UserError("auto_missing", "Сначала рассчитайте автоматический кадр для этого интервала.");
        cmd.Parameters.Clear();
        cmd.CommandText = "UPDATE edit_clip SET revision=revision+1,decision=$edit WHERE project_id=$project AND id=$id AND revision=$revision";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
        cmd.Parameters.AddWithValue("$edit", JsonSerializer.Serialize(edit));
        cmd.Parameters.AddWithValue("$revision", revision);
        if (cmd.ExecuteNonQuery() != 1) throw ChangedEdit();
        var result = ReadEdit(cmd, project, id);
        if (cue != null)
        {
            var index = Array.FindIndex(result.Cues, c => c.Index == cue.Index);
            if (index < 0 || string.IsNullOrWhiteSpace(cue.Text) || cue.Text.Length > 8000) throw InvalidEdit();
            if (cue.SourceStartUs.HasValue != cue.SourceEndUs.HasValue ||
                (cue.SourceStartUs is long from && cue.SourceEndUs is long to &&
                 (from < 0 || from >= to || to > project.DurationUs || edit.ClipTime(from,to) == null)))
                throw new UserError("cue_range", "Укажите границы фразы внутри видео с видимой частью в клипе.");
            cmd.CommandText = """
                INSERT INTO edit_text(clip_id,segment_index,body,source_start_us,source_end_us)
                VALUES($id,$index,$body,$from,$to)
                ON CONFLICT(clip_id,segment_index) DO UPDATE SET body=excluded.body,
                    source_start_us=COALESCE(excluded.source_start_us,edit_text.source_start_us),
                    source_end_us=COALESCE(excluded.source_end_us,edit_text.source_end_us)
                """;
            cmd.Parameters.AddWithValue("$index", cue.Index);
            cmd.Parameters.AddWithValue("$body", cue.Text);
            cmd.Parameters.AddWithValue("$from", (object?)cue.SourceStartUs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$to", (object?)cue.SourceEndUs ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            result = ReadEdit(cmd, project, id);
        }
        TouchEditorProject(cmd);
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return result;
    }
}
