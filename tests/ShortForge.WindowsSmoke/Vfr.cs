using System.IO;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunVfr(string phase, string playable, string unsupported)
    {
        bool anamorphic = phase.StartsWith("vfr-aspect", StringComparison.Ordinal);
        if (phase is "vfr" or "vfr-aspect")
        {
            await Contains("Добавьте первое видео");
            await DropFile(playable);
            await CheckPortrait("original", anamorphic);
            await Click("Все проекты");
            await DropFile(unsupported);
            await Contains("Предпросмотр недоступен");
            await Click("Создать прокси");
        }
        else
        {
            var name = Literal(Path.GetFileNameWithoutExtension(unsupported));
            var row = $"Array.from(document.querySelectorAll('.project-row')).find(b=>b.innerText.includes({name}))";
            await Wait(row, "Saved project");
            if (anamorphic)
                await Wait("document.querySelectorAll('.thumb img').length === 2 && Array.from(document.querySelectorAll('.thumb img')).every(i => i.naturalWidth === 81 && i.naturalHeight === 180)", "Two square-pixel portrait thumbnails");
            await Eval($"{row}.click()");
        }
        if (phase == "vfr-repair")
        {
            await Contains("Не удалось воспроизвести сохранённый прокси");
            await Layout("proxy-playback-error");
            await Click("Пересоздать прокси");
        }
        if (phase == "vfr-upgrade")
        {
            await Contains("Обновить прокси");
            await CheckPortrait("legacy", false);
            await Click("Обновить прокси");
            await Wait("!Array.from(document.querySelectorAll('button')).some(b=>b.textContent.includes('Обновить прокси'))",
                "Legacy proxy replaced after validation");
        }
        await Contains("Предпросмотр · Прокси");
        await CheckFrameIntervals(phase is "vfr" or "vfr-aspect", 48, 26, 47);
        await CheckPortrait(phase + "-proxy", anamorphic);
        if (phase.StartsWith("vfr-repair", StringComparison.Ordinal))
        {
            var url = System.Text.Json.JsonSerializer.Deserialize<string>(await Eval("document.querySelector('video').currentSrc"));
            File.WriteAllText(Path.Combine(output, phase + "-name.txt"), new Uri(url!).Segments[^1]);
        }
        Console.WriteLine("PASS " + phase);
    }

    private async Task CheckFrameIntervals(bool measure, int frames, double minMs, double maxMs)
    {
        if (measure) await Click("Измерить интервалы кадров");
        await Contains("Интервалы в начале");
        const string read = "Object.fromEntries(Array.from(document.querySelectorAll('.props dl div'), e => [e.querySelector('dt').textContent,e.querySelector('dd').textContent]))";
        string numbers = System.Text.Json.JsonSerializer.Serialize(new[] { minMs, maxMs });
        await Wait($$"""
            (() => {
                const props = {{read}}, [min, max] = {{numbers}};
                const fmt = n => n.toLocaleString('ru-RU', { maximumSignificantDigits: 6 });
                return props['Измерено кадров'] === '{{frames}}' &&
                    props['Интервалы в начале'] === (min === max ? fmt(min) : fmt(min)+'–'+fmt(max))+' мс';
            })()
            """, "Measured intervals match the original timestamps");
        Console.WriteLine($"Frame intervals: measured={measure}, frames={frames}, displayed={await Eval(read + "['Интервалы в начале']")}");
    }

    private async Task CheckPortrait(string label, bool anamorphic)
    {
        await Contains("320 × 180");
        await Contains("90°");
        if (anamorphic)
        {
            await Contains("5:4");
            await Contains("20:9");
            await Wait("Math.abs(document.querySelector('video')?.videoWidth / document.querySelector('video')?.videoHeight - 9/20) < 0.002",
                "Display aspect 9:20 after rotation");
        }
        else await Wait("document.querySelector('video')?.videoWidth === 180 && document.querySelector('video').videoHeight === 320",
            "Portrait 180x320 after rotation");
        foreach (var (seconds, bottomChannel) in new[] { ("0.70", 0), ("1.40", 1) })
        {
            await Eval($"document.querySelector('video').currentTime={seconds}");
            // Encoded left half changes red -> green; right half is blue. After +90°
            // the right half is on top. Inspect actual decoded pixels after seeking.
            await Wait($$"""
                (() => {
                  const v=document.querySelector('video');
                  if (!v || v.seeking || v.readyState < 2 || Math.abs(v.currentTime-{{seconds}}) > .02) return false;
                  const canvas=document.createElement('canvas');
                  canvas.width=2; canvas.height=4;
                  const ctx=canvas.getContext('2d');
                  ctx.drawImage(v,0,0,2,4);
                  const top=ctx.getImageData(1,0,1,1).data, bottom=ctx.getImageData(1,3,1,1).data;
                  return top[2]>180 && top[0]<70 && bottom[{{bottomChannel}}]>180 && bottom[2]<70;
                })()
                """, "Expected orientation and frame colour at " + seconds);
        }
        await Layout(label + "-portrait");
        Console.WriteLine($"{label}: portrait geometry and coloured regions verified at 0.70 and 1.40 seconds.");
    }
}
