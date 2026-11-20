using System.Text.Json;

namespace ShortForge.Core;

public sealed record BatchRequest(Guid[] ClipIds, int[] CandidateStarts, int Fps, bool IncludeSrt, bool IncludeManifest, bool NormalizeAudio, bool PreferNvenc);

public sealed record BridgeRequest(Guid Id, string Type, Guid? ProjectId = null, Guid? JobId = null, int? AudioTrack = null, Guid? ProxyId = null,
    string? Language = null, ClipSearch? Search = null, Guid? SelectionId = null, int? StartId = null, string? ClipState = null,
    Guid? ClipId = null, int Revision = 0, EditDecision? Edit = null, CueChange? Cue = null, long OffsetUs = 0,
    BatchRequest? Batch = null, int IntervalMs = 0)
{
    public const string Host = "app.shortforge.local";
    public const string Page = "https://" + Host + "/index.html";
    public static bool IsPage(string source) => string.Equals(source, Page, StringComparison.Ordinal);

    private static bool HasFields(JsonElement value, params string[] fields) =>
        value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == fields.Length &&
        fields.All(name => value.TryGetProperty(name, out _));

    public static BridgeRequest Parse(string json)
    {
        static UserError Invalid() => new("bad_request", "Не удалось прочитать запрос интерфейса.");
        if (json.Length > 65536) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            if (!HasFields(root, "version", "id", "type", "payload") ||
                root.GetProperty("version").GetInt32() != 1 ||
                !Guid.TryParseExact(root.GetProperty("id").GetString(), "D", out var id) || id == Guid.Empty)
                throw Invalid();
            var type = root.GetProperty("type").GetString();
            var payload = root.GetProperty("payload");
            if (json.Length > 4096 && type is not ("editor.save" or "exports.start")) throw Invalid();
            if (type is "editor.create" or "editor.open" or "editor.save" or "editor.preview" or "editor.srt" or "editor.captionVideo" or "editor.autoFrame")
            {
                Guid ReadId(string field) => payload.TryGetProperty(field, out var item) &&
                    Guid.TryParseExact(item.GetString(), "D", out var value) && value != Guid.Empty ? value : throw Invalid();
                var projectId = ReadId("projectId");
                if (type == "editor.create")
                {
                    if (HasFields(payload, "projectId")) return new(id, type, projectId);
                    if (!HasFields(payload, "projectId", "selectionId", "startId") ||
                        !payload.GetProperty("startId").TryGetInt32(out int start) || start < 0) throw Invalid();
                    return new(id, type, projectId, SelectionId: ReadId("selectionId"), StartId: start);
                }
                var clipId = ReadId("clipId");
                if (type == "editor.autoFrame")
                {
                    if (!HasFields(payload, "projectId", "clipId", "revision", "intervalMs") ||
                        !payload.GetProperty("revision").TryGetInt32(out var version) || version < 1 ||
                        !payload.GetProperty("intervalMs").TryGetInt32(out var interval) || interval is < 100 or > 2000)
                        throw Invalid();
                    return new(id, type, projectId, ClipId: clipId, Revision: version, IntervalMs: interval);
                }
                if (type == "editor.open" && HasFields(payload, "projectId", "clipId"))
                    return new(id, type, projectId, ClipId: clipId);
                if (type is "editor.preview" or "editor.srt" or "editor.captionVideo")
                {
                    bool preview = type == "editor.preview";
                    if (!(preview ? HasFields(payload, "projectId","clipId","revision","offsetUs") :
                        HasFields(payload, "projectId","clipId","revision")) ||
                        !payload.GetProperty("revision").TryGetInt32(out int current) || current < 1) throw Invalid();
                    long offset = preview ? payload.GetProperty("offsetUs").GetInt64() : 0;
                    if (offset < 0) throw Invalid();
                    return new(id, type, projectId, ClipId: clipId, Revision: current, OffsetUs: offset);
                }
                if (type != "editor.save" || !HasFields(payload, "projectId", "clipId", "revision", "edit", "cue") ||
                    !payload.GetProperty("revision").TryGetInt32(out var revision) || revision < 1) throw Invalid();
                var edit = payload.GetProperty("edit");
                if (!HasFields(edit, "title", "startUs", "endUs", "frame", "x", "y") &&
                    !HasFields(edit, "title", "startUs", "endUs", "frame", "x", "y", "captionStyle")) throw Invalid();
                CaptionStyle? style = null;
                if (edit.TryGetProperty("captionStyle", out var s) && s.ValueKind != JsonValueKind.Null)
                {
                    if (!HasFields(s, "preset","size","color","outline","background","position")) throw Invalid();
                    style = new(s.GetProperty("preset").GetString() ?? "", s.GetProperty("size").GetInt32(),
                        s.GetProperty("color").GetString() ?? "", s.GetProperty("outline").GetInt32(),
                        s.GetProperty("background").GetBoolean(), s.GetProperty("position").GetInt32());
                }
                var decision = new EditDecision(edit.GetProperty("title").GetString() ?? "", edit.GetProperty("startUs").GetInt64(),
                    edit.GetProperty("endUs").GetInt64(), edit.GetProperty("frame").GetString() ?? "",
                    edit.GetProperty("x").GetDouble(), edit.GetProperty("y").GetDouble(), style);
                if (!decision.Fits(long.MaxValue)) throw Invalid();
                var cue = payload.GetProperty("cue");
                CueChange? change = null;
                if (cue.ValueKind != JsonValueKind.Null)
                {
                    bool timed = HasFields(cue, "index","text","sourceStartUs","sourceEndUs");
                    if ((!timed && !HasFields(cue, "index","text")) || !cue.GetProperty("index").TryGetInt32(out int index) || index < 0 ||
                        cue.GetProperty("text").GetString() is not { Length: > 0 and <= 8000 } body || string.IsNullOrWhiteSpace(body)) throw Invalid();
                    long? from = timed ? cue.GetProperty("sourceStartUs").GetInt64() : null;
                    long? to = timed ? cue.GetProperty("sourceEndUs").GetInt64() : null;
                    if (timed && (from < 0 || to <= from)) throw Invalid();
                    change = new(index, body, from, to);
                }
                return new(id, type, projectId, ClipId: clipId, Revision: revision, Edit: decision, Cue: change);
            }
            if (type == "exports.start")
            {
                if (!HasFields(payload, "projectId", "selectionId", "candidateStarts", "clipIds", "fps", "includeSrt", "includeManifest", "normalizeAudio", "preferNvenc"))
                    throw Invalid();
                Guid ReadGuid(JsonElement element) => Guid.TryParseExact(element.GetString(), "D", out var guid) &&
                    guid != Guid.Empty ? guid : throw Invalid();
                var items = payload.GetProperty("clipIds");
                if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100) throw Invalid();
                var clips = items.EnumerateArray().Select(ReadGuid).ToArray();
                var selection = payload.GetProperty("selectionId");
                Guid? selectionId = selection.ValueKind == JsonValueKind.Null ? null : ReadGuid(selection);
                var candidates = payload.GetProperty("candidateStarts");
                if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 10) throw Invalid();
                var starts = candidates.EnumerateArray().Select(value => value.GetInt32()).ToArray();
                if (selectionId.HasValue != (starts.Length > 0) || starts.Any(start => start < 0) ||
                    starts.Distinct().Count() != starts.Length) throw Invalid();
                int fps = payload.GetProperty("fps").GetInt32();
                if (fps is not (30 or 60) || (clips.Length == 0 && selectionId is null) || clips.Distinct().Count() != clips.Length)
                    throw Invalid();
                var options = new BatchRequest(clips, starts, fps, payload.GetProperty("includeSrt").GetBoolean(),
                    payload.GetProperty("includeManifest").GetBoolean(), payload.GetProperty("normalizeAudio").GetBoolean(),
                    payload.GetProperty("preferNvenc").GetBoolean());
                return new(id, type, ReadGuid(payload.GetProperty("projectId")), SelectionId: selectionId, Batch: options);
            }
            if (type == "clips.mark")
            {
                if (!HasFields(payload,"projectId","selectionId","startId","state") ||
                    !Guid.TryParseExact(payload.GetProperty("projectId").GetString(),"D",out var projectId) || projectId==Guid.Empty ||
                    !Guid.TryParseExact(payload.GetProperty("selectionId").GetString(),"D",out var selectionId) || selectionId==Guid.Empty ||
                    !payload.GetProperty("startId").TryGetInt32(out int start) || start<0) throw Invalid();
                var state=payload.GetProperty("state").GetString();
                if (state is not ("offered" or "selected" or "rejected")) throw Invalid();
                return new(id,type,projectId,SelectionId:selectionId,StartId:start,ClipState:state);
            }
            if (type == "clips.find")
            {
                if (!HasFields(payload, "projectId", "mode", "minSeconds", "maxSeconds", "count") ||
                    !Guid.TryParseExact(payload.GetProperty("projectId").GetString(), "D", out var projectId) || projectId == Guid.Empty ||
                    !payload.GetProperty("minSeconds").TryGetInt32(out var min) || !payload.GetProperty("maxSeconds").TryGetInt32(out var max) ||
                    !payload.GetProperty("count").TryGetInt32(out var count)) throw Invalid();
                var search = new ClipSearch(payload.GetProperty("mode").GetString() ?? "", min, max, count);
                if (!search.IsValid) throw Invalid();
                return new(id, type, projectId, Search: search);
            }
            if (type == "transcript.start")
            {
                if (!HasFields(payload, "projectId", "language", "audioTrack") ||
                    !Guid.TryParseExact(payload.GetProperty("projectId").GetString(), "D", out var projectId) || projectId == Guid.Empty ||
                    !payload.GetProperty("audioTrack").TryGetInt32(out var audioTrack) || audioTrack < 0) throw Invalid();
                var language = payload.GetProperty("language").GetString();
                if (language is not ("auto" or "ru" or "en" or "az")) throw Invalid();
                return new(id, type, projectId, AudioTrack: audioTrack, Language: language);
            }
            if (type is "projects.open" or "media.relink" or "media.createProxy" or "media.rebuildProxy" or "media.measureFrameIntervals")
            {
                int? track = null;
                Guid? proxyId = null;
                if (type == "media.rebuildProxy")
                {
                    if (!HasFields(payload, "projectId", "proxyId") ||
                        !Guid.TryParseExact(payload.GetProperty("proxyId").GetString(), "D", out var proxy) || proxy == Guid.Empty) throw Invalid();
                    proxyId = proxy;
                }
                else if (type == "media.createProxy" && HasFields(payload, "projectId", "audioTrack"))
                {
                    if (!payload.GetProperty("audioTrack").TryGetInt32(out var index) || index < 0) throw Invalid();
                    track = index;
                }
                else if (!HasFields(payload, "projectId")) throw Invalid();
                if (!Guid.TryParseExact(payload.GetProperty("projectId").GetString(), "D", out var project) || project == Guid.Empty)
                    throw Invalid();
                return new(id, type, project, AudioTrack: track, ProxyId: proxyId);
            }
            if (type is "editor.flushed" or "jobs.cancel" or "exports.cancel" or "exports.retry" or "exports.openFolder")
            {
                if (!HasFields(payload, "jobId") ||
                    !Guid.TryParseExact(payload.GetProperty("jobId").GetString(), "D", out var job) || job == Guid.Empty)
                    throw Invalid();
                return new(id, type, JobId: job);
            }
            if (type is not ("projects.list" or "exports.list" or "exports.resume" or "media.pick" or "media.importDropped" or "settings.get" or "settings.pickTools" or "settings.useAutomaticTools"))
                throw new UserError("unknown_command", "Эта команда недоступна.");
            if (!HasFields(payload)) throw Invalid();
            return new(id, type, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
        {
            throw Invalid();
        }
    }

    public static Guid? ReplyId(string json)
    {
        if (json.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String && Guid.TryParseExact(id.GetString(), "D", out var guid) ? guid : null;
        }
        catch (JsonException) { return null; }
    }
}
