using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public sealed class FaceAnalysisTests
{
    [WindowsFaceMediaFact]
    public async Task WindowsDetectorReadsOnlySelectedIntervalReportsCutAndCanCancel()
    {
        var work = Directory.CreateTempSubdirectory("Кадры O'Brien ");
        try
        {
            var source = Path.Combine(work.FullName, new string('я', 80) + ".mkv");
            var ffmpeg = LibraryTests.Ffmpeg!;
            await MediaProcess.RunAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
                "color=black:s=320x180:r=1:d=1[b];color=white:s=320x180:r=1:d=2[w];[b][w]concat=n=2:v=1:a=0",
                "-threads", "1", "-c:v", "ffv1", source], TimeSpan.FromSeconds(20), 0, default);
            var input = await MediaProbe.ReadAsync(LibraryTests.Ffprobe!, source, default);
            var samples = await FaceAnalysis.ReadAsync(source, input, 375_000, 2_210_000, 250, ffmpeg, null, default);
            Assert.Equal(Enumerable.Range(0, 8).Select(i => 375_000L + 250_000L * i), samples.Select(s => s.TimeUs));
            Assert.All(samples, s => Assert.Empty(s.Faces));
            Assert.Equal(1_125_000, Assert.Single(samples, s => s.SceneCut).TimeUs);
            // Both samples lie inside the display duration of the last decoded frame.
            var tail = await FaceAnalysis.ReadAsync(source, input, 2_375_000, 2_800_000, 250, ffmpeg, null, default);
            Assert.Equal([2_375_000L, 2_625_000L], tail.Select(s => s.TimeUs));

            using var cts = new CancellationTokenSource();
            var cancel = new CancelWhenFrameArrives(cts);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                FaceAnalysis.ReadAsync(source, input, 0, 3_000_000, 100, ffmpeg, cancel, cts.Token));
            Assert.True(cancel.ReceivedFrame);
        }
        finally { work.Delete(recursive: true); }
    }

    private sealed class WindowsFaceMediaFactAttribute : FactAttribute
    {
        public WindowsFaceMediaFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires the Windows FaceAnalysis API.";
            else if (LibraryTests.Ffmpeg is null || LibraryTests.Ffprobe is null) Skip = "Requires FFmpeg and ffprobe.";
        }
    }

    private sealed class CancelWhenFrameArrives(CancellationTokenSource cts) : IProgress<ImportProgress>
    {
        public bool ReceivedFrame { get; private set; }
        public void Report(ImportProgress progress)
        {
            ReceivedFrame = true;
            cts.Cancel();
        }
    }
}
