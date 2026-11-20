using System.Text.Json;
using System.Diagnostics;
using System.Text;
using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public class TranscriptTests
{
    private static string Report(params (long From, long To, string Text)[] rows) => JsonSerializer.Serialize(new
    {
        model = new { type = "small", multilingual = true },
        result = new { language = "ru" },
        transcription = rows.Select(s => new { offsets = new { from = s.From, to = s.To }, text = s.Text })
    });

    [Fact]
    public void MillisecondsBecomeBoundedSourceMicrosecondsAndTextRemainsData()
    {
        var (_, segments) = Library.ParseSegments(Report((12, 79, " <слово> "), (79, 192, " два ")), 190_500);
        Assert.Equal(new SpeechSegment(12_000,79_000,"<слово>"), segments[0]);
        Assert.Equal(new SpeechSegment(79_000,190_500,"два"), segments[1]);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(80, 80)]
    [InlineData(1100, 1200)]
    [InlineData(1, long.MaxValue)]
    public void RejectsImpossibleTimes(long start, long end) =>
        Assert.Equal("asr_output", Assert.Throws<UserError>(() => Library.ParseSegments(Report((start,end,"x")),1_000_000)).Code);

    [Fact]
    public void DropsExactDuplicateButRejectsConflictingOverlap()
    {
        Assert.Single(Library.ParseSegments(Report((0,100,"x"),(0,100,"x")),500_000).Segments);
        Assert.Throws<UserError>(() => Library.ParseSegments(Report((0,100,"x"),(99,110,"y")),500_000));
    }

    [RealMediaFact]
    public async Task SelectedAudioKeepsItsDelayedStartOnTheVideoTimeline()
    {
        var dir = Directory.CreateTempSubdirectory("Дорожка O'Brien ");
        try
        {
            var input = Path.Combine(dir.FullName, "исходник.mkv");
            var wav = Path.Combine(dir.FullName, "для речи.wav");
            var args = new ProcessStartInfo(LibraryTests.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-v","error","-nostdin","-f","lavfi","-i","color=s=96x64:d=2",
                "-f","lavfi","-i","anullsrc=r=16000:cl=mono","-itsoffset","0.5","-f","lavfi","-i","sine=f=440:r=16000:d=1",
                "-map","0:v","-map","1:a","-map","2:a","-t","2","-c:v","mpeg4","-c:a","pcm_s16le",input }) args.ArgumentList.Add(arg);
            using var process = Process.Start(args)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            await Library.ExtractAudioAsync(input, 1, wav, LibraryTests.Ffmpeg!, LibraryTests.Ffprobe!, default);
            using var reader = new BinaryReader(File.OpenRead(wav));
            reader.BaseStream.Position = 12;
            while (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "data")
            {
                uint size = reader.ReadUInt32();
                reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
            int samples = reader.ReadInt32() / 2;
            var audible = Enumerable.Range(0,samples).Select(_ => (int)reader.ReadInt16()).ToArray();
            int onset = Array.FindIndex(audible, value => Math.Abs(value) > 150);
            Assert.InRange(onset, 7984, 8016);
            Assert.InRange(samples, 23984, 24016);
        }
        finally { dir.Delete(recursive: true); }
    }
}
