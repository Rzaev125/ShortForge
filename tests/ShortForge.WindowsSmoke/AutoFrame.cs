using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using ShortForge.Core;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunAutoFrame(string phase, string source)
    {
        string data = Path.Combine(output, "data");
        bool cases = phase == "auto-cases";
        var library = new Library(data);
        var sourceHash = HashFile(source);
        if (phase == "auto-restart")
        {
            await Wait("document.querySelector('.project-row')", "Saved project");
            await Eval("document.querySelector('.project-row').click()");
            await Click("Новый клип");
        }
        else
        {
            await Contains("Добавьте первое видео");
            await DropFile(source);
            await Click("Создать клип вручную");
        }
        await Wait("document.querySelector('.edit-frame video')?.videoWidth>0", "Live source in editor");
        var project = library.Open(library.List().Single().Id);
        var clipId = project.Edits.Single().Id;
        EditorClip Read() => library.OpenEdit(project.Id, clipId);
        async Task Persisted(Func<EditorClip, bool> expected, string why)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 45_000)
            {
                if (expected(Read())) return;
                if (await Eval("!!document.querySelector('dialog [role=alert]')") == "true") break;
                await Task.Delay(100);
            }
            throw new InvalidDataException(why + ": " + await Eval("document.querySelector('dialog').innerText"));
        }
        async Task Seek(double sourceSeconds)
        {
            string time = sourceSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            await Eval($"document.querySelector('.edit-frame video').currentTime={time}");
            await Wait($"document.querySelector('.edit-monitor output').textContent.endsWith('Исходник {time} с')", "Seek in editor");
        }
        var settings = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        string pathFile = Path.Combine(output, "trajectory.json");
        if (phase == "auto-restart")
        {
            var prior = JsonSerializer.Deserialize<EditorClip>(File.ReadAllText(pathFile), settings)!;
            bool ManualIsIntact() => Read().Edit is { Frame: "crop", X: .24 } edit &&
                JsonSerializer.Serialize(edit.Trajectory, settings) == JsonSerializer.Serialize(prior.Edit.Trajectory, settings);
            await Wait("document.querySelector('.edit-props input[type=range]')?.value==='0.24'", "Restored manual crop");
            await Seek(4.25);
            if (!ManualIsIntact()) throw new InvalidDataException("Manual crop or saved path changed at restart.");
            await Click("Вернуть автокадр");
            await Persisted(c => c.Edit.Frame == "auto", "Restore saved trajectory");
            if (JsonSerializer.Serialize(Read().Edit.Trajectory, settings) != JsonSerializer.Serialize(prior.Edit.Trajectory, settings))
                throw new InvalidDataException("Re-enabling the saved path recalculated it.");
            Console.WriteLine("PASS separate WPF process restored manual x=0.24 and original path; explicit restore returned to that path.");
            return;
        }
        if (!cases)
        {
            await Eval("""
                {const el=document.querySelector('.edit-times label:first-child input');el.focus();
                Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,'0.5');
                el.dispatchEvent(new Event('input',{bubbles:true}));el.blur();}
                """);
            await Persisted(c => c.Edit.StartUs == 500_000, "Nonzero clip In");
        }
        await Eval("""
            {const label=[...document.querySelectorAll('dialog label')].find(x=>x.textContent.includes('Интервал анализа'));
            const select=label.querySelector('select');select.value='250';select.dispatchEvent(new Event('change',{bubbles:true}));}
            """);
        await Click("Автокадр по лицу");
        await Persisted(c => c.Edit is { Frame: "auto", Trajectory.IntervalMs: 250 }, "Real face analysis");
        var clip = Read();
        var path = clip.Edit.Trajectory!;
        File.WriteAllText(pathFile, JsonSerializer.Serialize(clip, settings));
        Console.WriteLine($"PASS {phase} saved {path.Points.Length} points, selected interval {path.IntervalMs} ms, counts {path.Counts}.");
        var live = new List<object>();
        foreach (var t in cases ? new[] { 1d, 2.5, 5.5, 6.25, 9.5 } : new[] { 1d, 3, 4.25, 5, 6.25, 8 })
        {
            await Seek(t);
            var frame = JsonSerializer.Deserialize<JsonElement>(await Eval("""
                (()=>{const s=getComputedStyle(document.querySelector('.edit-frame video'));
                return {fit:s.objectFit,x:parseFloat(s.objectPosition)/100}})()
                """));
            live.Add(new { sourceSeconds = t, frame });
        }
        File.WriteAllText(Path.Combine(output, "live.json"), JsonSerializer.Serialize(live, settings));
        if (await Eval("!!document.querySelector('.edit-guides')") != "true")
            await Eval("document.querySelector('.edit-props input[type=checkbox]').click()");
        await Wait("document.querySelector('.edit-guides')", "Guides visible in editor");
        await Seek(cases ? 5.5 : 2.5);
        await Click("Точный предпросмотр");
        await Wait("document.querySelector('video.caption-rendered')?.videoWidth>0", "Rendered preview loaded in WebView2", 100);
        string exactStart = cases ? "5.500" : "2.000";
        await Wait($"document.querySelector('.caption-result figcaption').textContent.includes('{exactStart}')", "Clip-relative preview offset");
        File.Copy(Directory.GetFiles(Path.Combine(data, "caption-previews"), "*.mp4").Single(), Path.Combine(output, "preview.mp4"));
        await Eval("{const v=document.querySelector('video.caption-rendered');v.muted=true;void v.play()}");
        await Wait("document.querySelector('video.caption-rendered').currentTime>.2", "Rendered preview decoded by WebView2");
        await Eval("document.querySelector('video.caption-rendered').pause()");
        string destination = Path.Combine(output, "export.mp4");
        var owner = new WindowInteropHelper(window).Handle;
        var picker = Task.Run(() => CaptionSaveDialog.Complete(owner, "Сохранить клип MP4", destination));
        await Task.WhenAll(Click("Сохранить MP4"), picker).WaitAsync(TimeSpan.FromSeconds(30));
        await Wait("document.querySelector('#caption-video:not(:disabled)')", "Completed native export", 120);
        if (!File.Exists(destination)) throw new IOException("Validated MP4 was not saved.");
        if (!sourceHash.SequenceEqual(HashFile(source))) throw new IOException("Input hash changed.");
        if (!cases)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            foreach (var (width, height, zoom) in new[] { (1280, 800, 1.25), (1440, 900, 1.50) })
            {
                window.Width = width / dpi; window.Height = height / dpi; View.ZoomFactor = zoom / dpi;
                await Wait($"Math.abs(devicePixelRatio-{zoom.ToString(CultureInfo.InvariantCulture)})<.02 && document.querySelector('dialog').scrollWidth<=document.querySelector('dialog').clientWidth+1", "Editor fits");
                await Layout("auto-" + width);
                Console.WriteLine($"PASS {width}x{height}, native DPI {dpi * 96}, WebView zoom {zoom}.");
            }
            await Click("Заполнить");
            await Wait("document.querySelector('.edit-props input[type=range]')", "Manual crop slider mounted");
            await Eval("""
                {const el=document.querySelector('.edit-props input[type=range]');
                 Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,'0.24');
                 el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new KeyboardEvent('keyup',{key:'ArrowLeft',bubbles:true}));}
                """);
            await Persisted(c => c.Edit is { Frame: "crop", X: .24, Trajectory: not null }, "Manual correction persisted");
        }
        Console.WriteLine($"PASS {phase} original hash, moving preview, visible editor guides and native MP4 output.");
    }
}
