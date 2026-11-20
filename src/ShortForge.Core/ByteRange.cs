using System.Globalization;

namespace ShortForge.Core;

public readonly record struct ByteRange(long Start, long Count)
{
    public static ByteRange? Resolve(string? header, long size)
    {
        if (size <= 0) return null;
        if (header is null) return new(0, size);
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var ends = header[6..].Split('-');
        if (ends.Length != 2) return null;
        static bool Number(string s, out long n) =>
            long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        if (ends[0].Length == 0)
        {
            if (!Number(ends[1], out var suffix) || suffix == 0) return null;
            var count = Math.Min(suffix, size);
            return new(size - count, count);
        }
        if (!Number(ends[0], out var start) || start >= size) return null;
        var end = size - 1;
        if (ends[1].Length > 0 && (!Number(ends[1], out end) || end < start)) return null;
        return new(start, Math.Min(size - 1, end) - start + 1);
    }
}
