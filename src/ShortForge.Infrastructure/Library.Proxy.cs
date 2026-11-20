using System.Globalization;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed partial class Library
{
    private (string? Path, bool NeedsRefresh) ExistingProxy(string? key)
    {
        if (!Guid.TryParseExact(key, "N", out var id)) return (null, false);
        var stem = Path.Combine(proxyDirectory, id.ToString("N"));
        if (File.Exists(stem + ".v2.mp4")) return (stem + ".v2.mp4", false);
        if (File.Exists(stem + ".mp4")) return (stem + ".mp4", true);
        return (null, false);
    }

    private int[] ReadOfflineAudioTracks(Guid id, int trackCount)
    {
        using var connection = Connect();
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT audio_ordinal,proxy_id FROM proxy_track
            WHERE project_id=$id AND audio_ordinal BETWEEN 0 AND $last ORDER BY audio_ordinal
            """;
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        query.Parameters.AddWithValue("$last", trackCount - 1);
        using var reader = query.ExecuteReader();
        var tracks = new List<int>();
        while (reader.Read())
            if (ExistingProxy(reader.GetString(1)).Path != null) tracks.Add(reader.GetInt32(0));
        return tracks.ToArray();
    }

    public Task<MediaProject> CreateProxyAsync(Guid id, string probe, string ffmpeg, CancellationToken cancellationToken,
        IProgress<ImportProgress>? progress = null, int? audioTrack = null) =>
        CreateProxyAsync(id, () => (probe, ffmpeg), cancellationToken, progress, audioTrack);

    public async Task<MediaProject> CreateProxyAsync(Guid id, Func<(string Probe, string Ffmpeg)> resolveTools, CancellationToken cancellationToken,
        IProgress<ImportProgress>? progress = null, int? audioTrack = null, Guid? replaceProxy = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Open(id);
        int ordinal = audioTrack ?? project.ProxyAudioTrack;
        if (ordinal < 0 || (audioTrack.HasValue && ordinal >= project.AudioTracks))
            throw new UserError("audio_track", "В исходнике нет такой аудиодорожки.");
        if (replaceProxy.HasValue)
        {
            if (project.ProxyId != replaceProxy || ordinal != project.ProxyAudioTrack) throw ProxyChanged();
        }
        else
        {
            if (project.ProxyPath is not null && (!project.ProxyNeedsRefresh || !project.SourceAvailable) && ordinal == project.ProxyAudioTrack) return project;
            if (ActivateProxy(id, ordinal, cancellationToken, allowLegacy: !project.SourceAvailable) != null) return Open(id);
        }
        if (!project.SourceAvailable)
            throw new UserError("source_unavailable", "Найдите исходное видео проекта перед созданием копии.");
        var (probe, ffmpeg) = resolveTools();
        progress?.Report(new("fingerprint"));
        using var source = await OpenOriginalForProcessingAsync(id, cancellationToken);
        var metadata = await MediaProbe.ReadAsync(probe, source.Name, cancellationToken);
        if (metadata.IsHdr && metadata.Color?.CanToneMap != true)
            throw new UserError("proxy_hdr", "Для SDR-прокси нужны PQ или HLG, BT.2020, матрица BT.2020 NC и известный диапазон. Dolby Vision пока не поддерживается.");
        Directory.CreateDirectory(proxyDirectory);
        var cacheId = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(proxyDirectory, cacheId + ".partial.mp4");
        var final = Path.Combine(proxyDirectory, cacheId + ".v2.mp4");
        bool moved = false;
        try
        {
            // Preserve display aspect including non-square pixels. FFmpeg applies source
            // rotation before this filter. Keep original timestamps across VFR frames.
            const string scale = "scale=w='max(2,2*trunc(min(1280,min(iw*if(gt(sar,0),sar,1),720*dar))/2))':" +
                "h='max(2,2*trunc(min(720,min(ih,1280/dar))/2))',setsar=1";
            // Work in linear light for the tone curve. Limit float frame buffers to proxy dimensions.
            string videoFilter = scale;
            if (metadata.IsHdr)
            {
                // Linear units are multiples of 100 nits. PQ spans 10,000 nits, HLG uses a 1,000-nit reference.
                string peak = metadata.Color!.Transfer == "smpte2084" ? "100" : "10";
                videoFilter += ",zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=709," +
                    $"tonemap=hable:peak={peak}:desat=0,zscale=t=709:m=709:r=limited:dither=error_diffusion," +
                    "format=yuv420p,sidedata=mode=delete";
            }
            try
            {
                int lastPercent = -1;
                progress?.Report(new("proxy"));
                await MediaProcess.RunAsync(ffmpeg,
                    ["-v", "error", "-nostdin", "-nostats", "-progress", "pipe:1", "-n", "-protocol_whitelist", "file", "-format_whitelist",
                     MediaProbe.StandaloneFormats, "-threads", "2", "-i", source.Name, "-map",
                     "0:" + metadata.StreamIndex.ToString(CultureInfo.InvariantCulture), "-map",
                     metadata.AudioTracks == 0 ? "0:a?" : "0:a:" + ordinal.ToString(CultureInfo.InvariantCulture),
                     "-map_metadata", "-1", "-map_chapters", "-1", "-filter_threads", "2", "-vf", videoFilter,
                     "-fps_mode:v", "passthrough", "-enc_time_base:v", "demux", "-c:v", "libx264", "-threads:v", "2", "-preset", "veryfast",
                     "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-b:a", "128k",
                     "-movflags", "+faststart", "-f", "mp4", temporary], Timeout.InfiniteTimeSpan, 0, cancellationToken,
                    encodedUs =>
                    {
                        int percent = (int)Math.Min(99, encodedUs * 100.0 / metadata.DurationUs);
                        if (percent <= lastPercent) return;
                        lastPercent = percent;
                        progress?.Report(new("proxy", percent));
                    });
            }
            catch (UserError error) when (error.Code == "media_unreadable")
            {
                if (metadata.IsHdr)
                    throw new UserError("proxy_encode", "Не удалось создать SDR-прокси. Проверьте свободное место и поддержку zscale, tonemap, libx264 и AAC в выбранном FFmpeg.");
                throw new UserError("proxy_encode", "FFmpeg не создал прокси. Проверьте поддержку libx264/AAC и свободное место.");
            }
            progress?.Report(new("proxyVerify"));
            var result = await MediaProbe.ReadAsync(probe, temporary, cancellationToken);
            if (result.VideoCodec != "h264" || !result.Container.Split(',').Contains("mp4")
                || result.Width > 1280 || result.Height > 720 || result.AudioTracks != Math.Min(metadata.AudioTracks, 1)
                || (result.AudioTracks == 1 && result.FirstAudioCodec != "aac")
                || (metadata.IsHdr && (result.IsHdr || !result.IsLimitedBt709Yuv420))
                || Math.Abs(result.DurationUs - metadata.DurationUs) > 250_000)
                throw new UserError("proxy_invalid", "Параметры готовой копии отличаются от ожидаемых. Прокси не сохранён.");

            File.Move(temporary, final);
            moved = true;
            if (ActivateProxy(id, ordinal, cancellationToken, cacheId, replaceProxy: replaceProxy) != cacheId)
                File.Delete(final); // Another request already published this track.
        }
        catch
        {
            if (moved) File.Delete(final);
            throw;
        }
        finally { File.Delete(temporary); }
        return Open(id);
    }

    private static UserError ProxyChanged() => new("proxy_changed", "Выбранный прокси изменился. Откройте проект заново.");

    private string? ActivateProxy(Guid id, int ordinal, CancellationToken token, string? newId = null, bool allowLegacy = false, Guid? replaceProxy = null)
    {
        using var connection = Connect();
        using var tx = connection.BeginTransaction();
        using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        query.Parameters.AddWithValue("$track", ordinal);
        query.CommandText = "SELECT proxy_id FROM proxy_track WHERE project_id=$id AND audio_ordinal=$track";
        var cachedId = query.ExecuteScalar() as string;
        if (replaceProxy.HasValue)
        {
            query.CommandText = "SELECT proxy_id FROM media_asset WHERE project_id=$id";
            if (cachedId != replaceProxy.Value.ToString("N") || query.ExecuteScalar() as string != cachedId) throw ProxyChanged();
        }
        var cached = ExistingProxy(cachedId);
        bool reusable = !replaceProxy.HasValue && cached.Path != null && (!cached.NeedsRefresh || allowLegacy);
        var selected = reusable ? cachedId : newId;
        if (selected == null) return null;
        query.Parameters.AddWithValue("$cache", selected);
        if (!reusable)
        {
            query.CommandText = """
                INSERT INTO proxy_track(project_id,audio_ordinal,proxy_id) VALUES ($id,$track,$cache)
                ON CONFLICT(project_id,audio_ordinal) DO UPDATE SET proxy_id=excluded.proxy_id
                """;
            query.ExecuteNonQuery();
        }
        query.Parameters.AddWithValue("$now", UtcNow());
        query.CommandText = """
            UPDATE media_asset SET proxy_id=$cache WHERE project_id=$id;
            UPDATE project SET updated_utc=$now WHERE id=$id;
            """;
        query.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return selected;
    }
}
