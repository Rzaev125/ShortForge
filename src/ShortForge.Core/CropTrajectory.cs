namespace ShortForge.Core;

// Samples use source time and coordinates on the displayed (autorotated, square-pixel) image.
public sealed record FaceBox(double X, double Y, double Width, double Height);
public sealed record FaceSample(long TimeUs, bool SceneCut, FaceBox[] Faces);
public sealed record FaceCounts(int Single, int Multiple, int Missing, int SceneCuts);

// Points use clip-relative time. X/Y specify a fraction of the available crop travel.
public sealed record CropPoint(long TimeUs, string Frame, double X, double Y, bool Cut);
public sealed record CropTrajectory(int IntervalMs, CropPoint[] Points, FaceCounts Counts)
{
    public const int MaxPoints = 3600;

    public bool Fits(long lengthUs)
    {
        if (IntervalMs is < 100 or > 2000 || Points is not { Length: > 0 and <= MaxPoints } ||
            Counts is not { Single: >= 0, Multiple: >= 0, Missing: >= 0, SceneCuts: >= 0 } ||
            (long)Counts.Single + Counts.Multiple + Counts.Missing != Points.Length || Counts.SceneCuts > Points.Length)
            return false;
        long previous = -1;
        foreach (var point in Points)
        {
            if (point is not { Frame: "crop" or "fit", X: >= 0 and <= 1, Y: >= 0 and <= 1 } ||
                point.TimeUs <= previous || point.TimeUs >= lengthUs || previous < 0 && point.TimeUs != 0)
                return false;
            previous = point.TimeUs;
        }
        return true;
    }

    public static CropTrajectory Build(IReadOnlyList<FaceSample> samples, long startUs, long endUs,
        double displayAspect, int intervalMs)
    {
        if (!double.IsFinite(displayAspect) || displayAspect <= 0 || samples.Count is < 1 or > MaxPoints ||
            intervalMs is < 100 or > 2000 || startUs < 0 || startUs >= endUs || samples[0].TimeUs != startUs)
            throw InvalidSamples();
        const double outputAspect = 9d / 16;
        double cropWidth = Math.Min(1, outputAspect / displayAspect);
        double cropHeight = Math.Min(1, displayAspect / outputAspect);
        var points = new CropPoint[samples.Count];
        int single = 0, multiple = 0, missing = 0, cuts = 0;
        for (int first = 0; first < samples.Count;)
        {
            int end = first + 1;
            while (end < samples.Count && !samples[end].SceneCut) end++;
            bool group = false;
            for (int i = first; i < end; ++i)
                group |= samples[i].Faces.Length > 1;

            double x = .5, y = .5;
            long? lastFaceUs = null;
            bool exceededSpeed = false;
            for (int i = first; i < end; i++)
            {
                var sample = samples[i];
                if (sample.TimeUs >= endUs || i > 0 && sample.TimeUs <= samples[i - 1].TimeUs ||
                    sample.Faces.Any(b => b is not { X: >= 0 and < 1, Y: >= 0 and < 1, Width: > 0 and <= 1,
                        Height: > 0 and <= 1 } || b.X + b.Width > 1.000001 || b.Y + b.Height > 1.000001))
                    throw InvalidSamples();
                if (sample.SceneCut) cuts++;
                if (sample.Faces.Length == 0) missing++;
                else if (sample.Faces.Length == 1) single++;
                else multiple++;

                if (!group && sample.Faces is [var face] && face.Width <= cropWidth && face.Height <= cropHeight)
                {
                    double targetX = Position(face.X + face.Width / 2, cropWidth);
                    double targetY = Position(face.Y + face.Height / 2, cropHeight);
                    if (lastFaceUs.HasValue)
                    {
                        double dt = (sample.TimeUs - samples[i - 1].TimeUs) / 1_000_000d;
                        var nextX = Follow(x, targetX, dt, cropWidth, face.X, face.Width);
                        var nextY = Follow(y, targetY, dt, cropHeight, face.Y, face.Height);
                        exceededSpeed |= !nextX.HasValue || !nextY.HasValue;
                        x = nextX ?? x;
                        y = nextY ?? y;
                    }
                    else { x = targetX; y = targetY; }
                    lastFaceUs = sample.TimeUs;
                }
                else if (sample.Faces.Length != 0 || sample.TimeUs - lastFaceUs > 750_000)
                    lastFaceUs = null;
                points[i] = new(sample.TimeUs - startUs, lastFaceUs.HasValue ? "crop" : "fit",
                    lastFaceUs.HasValue ? x : .5, lastFaceUs.HasValue ? y : .5, i == first);
            }
            // A jump can be a detector identity change. Show the whole shot if no bounded move
            // can keep the observed face visible, instead of chasing a new location.
            if (exceededSpeed)
                for (int i = first; i < end; i++)
                    points[i] = points[i] with { Frame = "fit", X = .5, Y = .5 };
            first = end;
        }
        return new(intervalMs, points, new(single, multiple, missing, cuts));
    }

    private static UserError InvalidSamples() => new("auto_samples", "Нет корректной последовательности кадров для анализа.");

    private static double Position(double center, double visible) => visible >= 1 ? .5 :
        Math.Clamp((center - visible / 2) / (1 - visible), 0, 1);

    private static double? Follow(double current, double target, double dt, double visible, double faceStart, double faceSize)
    {
        if (visible >= 1) return .5;
        double travel = 1 - visible;
        double distance = (target - current) * travel;
        double step = Math.Abs(distance) <= .018 * visible ? 0 : distance * (1 - Math.Exp(-dt / .22));
        double from = current * travel;
        double lower = Math.Max(Math.Max(0, from - .55 * dt), faceStart + faceSize - visible);
        double upper = Math.Min(Math.Min(travel, from + .55 * dt), faceStart);
        return lower > upper + 1e-8 ? null : Math.Clamp(Math.Clamp(from + step, Math.Min(lower, upper), upper) / travel, 0, 1);
    }
}
