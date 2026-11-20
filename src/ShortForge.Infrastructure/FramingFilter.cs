using System.Globalization;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

internal static class FramingFilter
{
    private const string Fit =
        "scale=1080:1920:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,pad=1080:1920:(ow-iw)/2:(oh-ih)/2";

    private static string Crop(string x, string y) =>
        "crop=w='max(2,2*trunc(min(iw,ih*9/16/if(gt(sar,0),sar,1))/2))':" +
        $"h='max(2,2*trunc(min(ih,iw*if(gt(sar,0),sar,1)*16/9)/2))':x='(iw-ow)*({x})':y='(ih-oh)*({y})',scale=1080:1920,setsar=1";

    private static string Number(double d) => d.ToString("0.#########", CultureInfo.InvariantCulture);

    internal static string Build(EditDecision edit, long offsetUs, long durationUs)
    {
        if (edit.Frame == "fit") return Fit;
        if (edit.Frame == "crop") return Crop(Number(edit.X), Number(edit.Y));
        var points = edit.Trajectory!.Points;
        int first = Array.FindLastIndex(points, p => p.TimeUs <= offsetUs);
        int last = Array.FindLastIndex(points, p => p.TimeUs < offsetUs + durationUs);
        bool crops = false, fits = false;
        for (int i = first; i <= last; i++)
        {
            if (points[i].Frame == "fit") fits = true;
            else crops = true;
        }
        if (!crops) return Fit;

        // Use a balanced decision tree. Long clips stay below FFmpeg's expression nesting limit.
        // Merge constant subtrees so a static axis costs nothing per frame.
        string Piecewise(int from, int through, Func<int, string> interval)
        {
            if (from == through) return interval(from);
            int middle = from + (through - from + 1) / 2;
            string left = Piecewise(from, middle - 1, interval);
            string right = Piecewise(middle, through, interval);
            return left == right ? left : $"if(lt(t,{Time(points[middle])}),{left},{right})";
        }
        string Time(CropPoint p) => Number((p.TimeUs - offsetUs) / 1_000_000d);
        string Axis(int i, Func<CropPoint, double> value)
        {
            var p = points[i];
            string v = Number(value(p));
            if (i + 1 == points.Length || p.Frame == "fit") return v;
            var next = points[i + 1];
            if (next.Cut || next.Frame == "fit" || value(next) == value(p)) return v;
            double slope = (value(next) - value(p)) * 1_000_000d / (next.TimeUs - p.TimeUs);
            return $"{v}+({Number(slope)})*(t-({Time(p)}))";
        }

        var crop = Crop(Piecewise(first, last, i => Axis(i, p => p.X)),
            Piecewise(first, last, i => Axis(i, p => p.Y)));
        if (!fits) return crop;
        string enabled = Piecewise(first, last, i => points[i].Frame == "crop" ? "1" : "0");
        return $"split[wide][tracked];[wide]{Fit}[full];[tracked]{crop}[portrait];" +
            $"[full][portrait]overlay=enable='{enabled}':format=auto";
    }
}
