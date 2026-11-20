using System.Globalization;
using System.Text.Json;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

public sealed partial class Library
{
    public async Task<EditorClip> AutoFrameAsync(Guid projectId, Guid clipId, int revision, int intervalMs,
        string ffmpeg, string ffprobe, IProgress<ImportProgress>? progress, CancellationToken token)
    {
        if (intervalMs is < 100 or > 2000)
            throw new UserError("auto_interval", "Выберите интервал анализа от 100 до 2000 мс.");
        await captionRenderer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var clip = OpenEdit(projectId, clipId);
            if (revision != clip.Revision) throw ChangedEdit();
            if (clip.Edit.EndUs - clip.Edit.StartUs > (long)CropTrajectory.MaxPoints * intervalMs * 1000)
                throw new UserError("auto_length", "Увеличьте интервал анализа или сократите клип до 3600 кадров анализа.");
            using var source = await OpenOriginalForProcessingAsync(projectId, token).ConfigureAwait(false);
            var input = await MediaProbe.ReadAsync(ffprobe, source.Name, token).ConfigureAwait(false);
            double aspect = DisplayAspect(input);
            var samples = await FaceAnalysis.ReadAsync(source.Name, input, clip.Edit.StartUs, clip.Edit.EndUs,
                intervalMs, ffmpeg, progress, token).ConfigureAwait(false);
            var path = CropTrajectory.Build(samples, clip.Edit.StartUs, clip.Edit.EndUs, aspect, intervalMs);
            return WriteEdit(projectId, clipId, revision, clip.Edit with { Frame = "auto" }, null, path, token);
        }
        finally { captionRenderer.Release(); }
    }

    private static double DisplayAspect(MediaProbe input)
    {
        using var metadata = JsonDocument.Parse(input.Json);
        var video = MediaProbe.SelectVideo(metadata.RootElement.GetProperty("streams").EnumerateArray());
        double aspect = (double)input.Width / input.Height;
        if (MediaProbe.ReadAnamorphicAspect(video)?.PixelRatio.Split(':') is [var num, var den])
            aspect *= double.Parse(num, CultureInfo.InvariantCulture) / double.Parse(den, CultureInfo.InvariantCulture);
        double turns = input.Rotation / 90;
        int quarterTurns = (int)Math.Round(turns);
        if (Math.Abs(turns - quarterTurns) > .001)
            throw new UserError("auto_rotation", "Для автоматического кадра поддерживаются повороты, кратные 90°.");
        return quarterTurns % 2 == 0 ? aspect : 1 / aspect;
    }
}
