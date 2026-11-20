using System.Globalization;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record FrameIntervals(int Frames, double MinMs, double MaxMs);

public sealed partial class Library
{
    public async Task<MediaProject> MeasureFrameIntervalsAsync(Guid id, string probe, CancellationToken token,
        IProgress<ImportProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var project = Open(id);
        if (project.FrameIntervals != null) return project;
        progress?.Report(new("fingerprint"));
        using var source = await OpenOriginalForProcessingAsync(id, token);
        progress?.Report(new("frameIntervals"));
        var metadata = await MediaProbe.ReadAsync(probe, source.Name, token);
        // The key/value writer omits nested HDR side-data containers from the timestamp report.
        var timestamps = await MediaProcess.RunAsync(probe,
            ["-v", "error", "-threads", "2", "-protocol_whitelist", "file", "-format_whitelist", MediaProbe.StandaloneFormats,
             "-select_streams", metadata.StreamIndex.ToString(CultureInfo.InvariantCulture), "-read_intervals", "%+#96",
             "-show_entries", "frame=pts:stream=time_base", "-show_optional_fields", "always",
             "-of", "default=noprint_wrappers=1", "-i", source.Name],
            TimeSpan.FromSeconds(30), 64 * 1024, token);
        var sample = ReadFrameIntervals(timestamps);
        using var connection = Connect();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO frame_sample(project_id,frames,min_ms,max_ms) VALUES ($id,$frames,$min,$max)
            ON CONFLICT(project_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$frames", sample.Frames);
        command.Parameters.AddWithValue("$min", sample.MinMs);
        command.Parameters.AddWithValue("$max", sample.MaxMs);
        command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return Open(id);
    }

    internal static FrameIntervals ReadFrameIntervals(string timestamps)
    {
        static UserError Invalid() => new("frame_timing_unknown", "Не удалось измерить интервалы по меткам показа первых кадров.");
        var lines = timestamps.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var bases = lines.Where(line => line.StartsWith("time_base=", StringComparison.Ordinal)).ToArray();
        if (bases.Length != 1 || bases[0]["time_base=".Length..].Split('/') is not [var n, var d] ||
            !int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) || numerator <= 0 ||
            !int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) || denominator <= 0) throw Invalid();
        long? previous = null;
        decimal min = decimal.MaxValue, max = 0;
        int count = 0;
        foreach (var line in lines.Where(line => line.StartsWith("pts=", StringComparison.Ordinal)).Take(48))
        {
            if (!long.TryParse(line.AsSpan(4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pts)) throw Invalid();
            if (previous.HasValue)
            {
                decimal interval = (decimal)pts - previous.Value;
                if (interval <= 0) throw Invalid();
                min = Math.Min(interval, min);
                max = Math.Max(interval, max);
            }
            count++;
            previous = pts;
        }
        if (count < 2) throw Invalid();
        double msPerTick = 1000d * numerator / denominator;
        return new(count, (double)min * msPerTick, (double)max * msPerTick);
    }
}
