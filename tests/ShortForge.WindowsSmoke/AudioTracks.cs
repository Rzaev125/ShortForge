using System.IO;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunAudioTracks(string phase, string source)
    {
        if (phase == "tracks")
        {
            await Contains("Добавьте первое видео");
            await DropFile(source);
            await Contains("Предпросмотр недоступен");
            await Wait("document.querySelector('#proxy-audio')?.options.length === 3", "All source tracks exposed");
            await Contains("Low · rus");
            await Contains("High · eng");
            await Click("Создать прокси");
            await CheckTone(1, 440);
            await SelectTrack(1);
            await CheckTone(2, 880);
            var second = await Eval("document.querySelector('video').currentSrc");
            await SelectTrack(0);
            await CheckTone(1, 440);
            await SelectTrack(1);
            await CheckTone(2, 880);
            if (second != await Eval("document.querySelector('video').currentSrc"))
                throw new InvalidDataException("Second proxy cache was rebuilt.");
            File.WriteAllText(Path.Combine(output, "selected-url.txt"), second);
        }
        else
        {
            await Wait("document.querySelectorAll('.project-row').length === 1", "Saved project");
            await Eval("document.querySelector('.project-row').click()");
            await Contains("Исходник не найден");
            await Contains("Для дорожек без сохранённого прокси нажмите «Найти файл»");
            const string options = "document.querySelector('#proxy-audio').options";
            await Wait($"{options}[2].disabled && {options}[2].text.includes('нужен исходник')", "Uncached third track requires source");
            if (phase == "tracks-restart")
            {
                await CheckTone(2, 880);
                if (File.ReadAllText(Path.Combine(output, "selected-url.txt")) != await Eval("document.querySelector('video').currentSrc"))
                    throw new InvalidDataException("Restart lost the selected proxy.");
                await Wait($"!{options}[0].disabled && !{options}[1].disabled", "Both cached tracks available offline");
                await SelectTrack(0);
                await CheckTone(1, 440);
                Console.WriteLine("Offline: switched from cached track 2 to 1 with unavailable FFmpeg tools.");
            }
            else
            {
                await CheckTone(1, 440);
                await Wait($"!{options}[0].disabled && {options}[1].disabled", "Only cache files still on disk are offered");
                await Layout("only-first-track-available");
                await Choose("Найти файл", source);
                await Wait($"Array.from({options}).every(o=>!o.disabled) && !document.querySelector('.missing')", "Relinking unlocks all source tracks");
                Console.WriteLine("Persisted track 1, missing cached file excluded, SHA-256 relink restored access to all tracks.");
            }
        }
        await Layout(phase);
        Console.WriteLine("PASS " + phase);
    }

    private async Task SelectTrack(int ordinal)
    {
        await Eval($"{{const s=document.querySelector('#proxy-audio'); s.value='{ordinal}'; s.dispatchEvent(new Event('change',{{bubbles:true}}));}}");
        await Click("Применить дорожку");
    }

    private async Task CheckTone(int number, int expectedHz)
    {
        await Contains("В прокси звучит дорожка " + number);
        await Wait($"document.querySelector('#proxy-audio')?.selectedIndex === {number - 1} && document.querySelector('video')?.videoWidth > 0", "Selected track and playable video");
        await Eval("""
            {
              const v = document.querySelector('video');
              const ctx = new AudioContext();
              const analyser = ctx.createAnalyser();
              analyser.fftSize = 4096;
              const quiet = ctx.createGain();
              quiet.gain.value = 0;
              ctx.createMediaElementSource(v).connect(analyser).connect(quiet).connect(ctx.destination);
              window.meter = {ctx, analyser};
              void ctx.resume(); void v.play();
            }
            """);
        const string peak = """
            (() => {
              const {ctx, analyser} = window.meter;
              const bins = new Float32Array(analyser.frequencyBinCount);
              analyser.getFloatFrequencyData(bins);
              const max = Math.max(...bins);
              return max > -65 ? bins.indexOf(max) * ctx.sampleRate / analyser.fftSize : 0;
            })()
            """;
        await Wait($"Math.abs({peak} - {expectedHz}) < 20", $"Expected audible {expectedHz} Hz in WebView2");
        Console.WriteLine($"Track {number}: decoded peak {await Eval(peak)} Hz (expected {expectedHz} Hz)");
        await Eval("document.querySelector('video').pause(); void window.meter.ctx.close()");
    }
}
