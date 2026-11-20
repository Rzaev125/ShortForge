using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunAsr(string phase, string path)
    {
        var data = Path.Combine(output, "data");
        bool restart = phase == "asr-restart";
        bool silence = phase == "asr-silence";
        if (restart)
        {
            await Wait("document.querySelectorAll('.project-row').length===1", "One saved ASR project");
            await Eval("document.querySelector('.project-row').click()");
            await Contains("Выберите фразу");
            Environment.SetEnvironmentVariable("SHORTFORGE_ASR_HOME", Path.Combine(output, "missing-asr-tools"));
        }
        else
        {
            await Contains("Добавьте первое видео");
            await DropFile(path);
        }
        string language = Environment.GetEnvironmentVariable("SHORTFORGE_TEST_LANGUAGE") ?? "en";
        await Wait("document.querySelector('#asr-language')", "ASR language control");
        await Eval($"{{ const s=document.querySelector('#asr-language'); s.value={Literal(language)}; s.dispatchEvent(new Event('change',{{bubbles:true}})); }}");
        int track = silence ? 0 : int.Parse(Environment.GetEnvironmentVariable("SHORTFORGE_TEST_TRACK") ?? "0", CultureInfo.InvariantCulture);
        if (track > 0)
        {
            await Wait("document.querySelector('#asr-track')", "ASR audio track control");
            await Eval($"const audio=document.querySelector('#asr-track'); audio.value='{track}'; audio.dispatchEvent(new Event('change',{{bubbles:true}}));");
        }
        if (!restart && !silence)
        {
            var existing = Process.GetProcessesByName("whisper-cli").Select(p => p.Id).ToHashSet();
            await Click("Распознать речь");
            await Contains("Распознаём речь на CPU");
            var child = await Task.Run(() =>
            {
                Process? found = null;
                if (!SpinWait.SpinUntil(() => (found = Process.GetProcessesByName("whisper-cli").FirstOrDefault(p => !existing.Contains(p.Id))) != null,
                    TimeSpan.FromSeconds(10))) throw new TimeoutException("No new whisper process");
                return found!;
            });
            await Click("Отменить");
            await Contains("Операция отменена");
            using (child) if (!child.WaitForExit(5000)) throw new InvalidOperationException("Cancelled whisper still running");
            if (Directory.EnumerateFileSystemEntries(Path.Combine(data, "asr-work")).Any())
                throw new InvalidOperationException("Cancelled audio or JSON remains");
            Console.WriteLine("Real whisper child exited on cancellation; work directory empty.");
        }
        var library = new Library(data);
        var id = library.List().Single().Id;
        var previous = library.Open(id).Transcript;
        await Click("Распознать речь");
        await Wait("document.querySelector('.speech-result') && !document.querySelector('.job') && !document.querySelector('[role=alert]') && !document.querySelector('#asr-language').disabled",
            "Saved real transcript", 240);
        var result = library.Open(id).Transcript ?? throw new InvalidDataException("Transcript not saved");
        if (result.AudioTrack != track || result.Language != language) throw new InvalidDataException("Saved ASR parameters differ");
        if (restart && JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(result))
            throw new InvalidDataException("Cache changed despite unavailable engine");
        if (silence)
        {
            await Contains("Речь не обнаружена");
            if (result.Segments.Length != 0) throw new InvalidDataException("Unexpected speech");
        }
        else
        {
            if (result.Segments.Length < 2) throw new InvalidDataException("Expected multiple phrases");
            int index = result.Segments.Length / 2;
            double target = result.Segments[index].StartUs / 1e6;
            await Eval($"document.querySelectorAll('.speech-result li button')[{index}].click()");
            await Wait($"!document.querySelector('video').seeking && Math.abs(document.querySelector('video').currentTime-{target.ToString("R", CultureInfo.InvariantCulture)}) < .06",
                "Player sought to ASR timestamp");
            Console.WriteLine($"Phrase seek: {target:F3} seconds.");
        }
        if (Directory.EnumerateFileSystemEntries(Path.Combine(data, "asr-work")).Any()) throw new IOException("Completed job retained scratch files");
        if (restart)
        {
            string other = language == "ru" ? "en" : "ru";
            await Eval($"{{ const s=document.querySelector('#asr-language'); s.value={Literal(other)}; s.dispatchEvent(new Event('change',{{bubbles:true}})); }}");
            await Wait($"document.querySelector('#asr-language').value === {Literal(other)}", "Changed language selection");
            await Click("Распознать речь");
            await Wait("document.querySelector('[role=alert]')?.textContent.includes('SHORTFORGE_ASR_HOME')", "Missing ASR setup explained");
            if (JsonSerializer.Serialize(result) != JsonSerializer.Serialize(library.Open(id).Transcript))
                throw new InvalidDataException("Failed request changed the completed transcript");
            Console.WriteLine("New language with unavailable tools was rejected; previous result retained.");
        }
        Console.WriteLine($"PASS {phase}: language={result.DetectedLanguage}, track={track+1}, segments={result.Segments.Length}, model={result.Model}, elapsedMs={result.ElapsedMs}, peakRamBytes={result.PeakRamBytes}, WebView2={Web.Environment.BrowserVersionString}");
    }
}
