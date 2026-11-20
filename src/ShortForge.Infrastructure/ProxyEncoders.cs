using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed record ProxyEncoders(bool Libx264, bool Aac)
{
    public static async Task<ProxyEncoders?> ReadAsync(string ffmpeg, CancellationToken token)
    {
        try
        {
            var listing = await MediaProcess.RunAsync(ffmpeg, ["-hide_banner", "-encoders"],
                TimeSpan.FromSeconds(5), 256 * 1024, token);
            return Parse(listing);
        }
        catch (UserError) { return null; }
    }

    internal static ProxyEncoders? Parse(string listing)
    {
        // Encoder names appear after six flags. Descriptions can mention other encoders.
        bool table = false, hasRows = false, libx264 = false, aac = false;
        foreach (var line in listing.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (!table) { table = line == "------"; continue; }
            if (line.Length == 0) continue;
            var fields = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0].Length != 6 || fields[0][0] is not ('V' or 'A' or 'S'))
                return null;
            hasRows = true;
            libx264 |= fields[0][0] == 'V' && fields[1] == "libx264";
            aac |= fields[0][0] == 'A' && fields[1] == "aac";
        }
        return hasRows ? new(libx264, aac) : null;
    }
}
