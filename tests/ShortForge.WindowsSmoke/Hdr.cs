using System.IO;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunHdr(string phase, string pq, string hlg, string sdr)
    {
        bool importing = phase == "hdr";
        if (importing) await Contains("Добавьте первое видео");
        else await Wait("document.querySelectorAll('.project-row').length === 3", "Three saved projects");

        async Task Open(string path)
        {
            if (importing) await DropFile(path);
            else
            {
                var name = Literal(Path.GetFileNameWithoutExtension(path));
                var row = $"Array.from(document.querySelectorAll('.project-row')).find(b=>b.innerText.includes({name}))";
                await Wait(row, "Saved project");
                await Eval($"{row}.click()");
            }
            await Contains(Path.GetFileNameWithoutExtension(path));
        }

        async Task CheckSdrCopy(string curve)
        {
            await Contains("HDR · " + curve + " · BT.2020");
            if (importing) await Click("Создать SDR-прокси");
            await Contains("Предпросмотр · SDR-прокси");
            await VerifyPlayback();
            await Wait("document.querySelector('video').currentSrc.includes('/proxy/')", "Decoded SDR proxy");
            var url = System.Text.Json.JsonSerializer.Deserialize<string>(await Eval("document.querySelector('video').currentSrc"));
            File.WriteAllText(Path.Combine(output, curve + "-copy.txt"), new Uri(url!).Segments[^1]);
        }

        await Open(pq);
        if (importing)
        {
            await Contains("Предпросмотр недоступен");
            await Eval("const s=document.querySelector('#proxy-audio'); s.value='1'; s.dispatchEvent(new Event('change',{bubbles:true}));");
        }
        await CheckSdrCopy("PQ");
        await Contains("В прокси звучит дорожка 2");
        await Layout(phase + "-pq");
        await Click("Все проекты");
        await Open(hlg);
        if (importing)
        {
            await VerifyPlayback();
            await Wait("document.querySelector('video').currentSrc.includes('/media/')", "Direct HDR playback");
        }
        await CheckSdrCopy("HLG");
        await Layout(phase + "-hlg");
        await Click("Все проекты");
        await Open(sdr);
        await Contains("bt709 · BT.2020");
        await Wait("!document.querySelector('#hdr-proxy-note')", "BT.2020 alone allows proxy");
        if (importing) await Click("Создать прокси");
        await Contains("Предпросмотр · Прокси");
        await VerifyPlayback();
        Console.WriteLine($"PASS {phase}: PQ track 2 and HLG play as SDR copies; wide-gamut SDR preview plays.");
    }
}
