using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunClips(string path)
    {
        static string Sec(long us) => (us / 1e6).ToString("R", CultureInfo.InvariantCulture);
        static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        var before = Hash(path);
        await Contains("Добавьте первое видео");
        await DropFile(path);
        await Click("Распознать речь");
        await Wait("document.querySelector('.speech-result li')", "New real ASR result", 300);
        var library = new Library(Path.Combine(output, "data"));
        var id = library.List().Single().Id;
        bool shortRange = Environment.GetEnvironmentVariable("SHORTFORGE_TEST_SHORT_RANGE") == "1";
        if (shortRange)
        {
            await Eval("""
                { const inputs = document.querySelectorAll('.clip-fields input');
                  const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                  [1,6,1].forEach((v,i) => { setValue.call(inputs[i], String(v)); inputs[i].dispatchEvent(new Event('input', {bubbles:true})); }); }
                """);
        }
        foreach (var mode in new[] { "structure", "semantic" })
        {
            var watch = Stopwatch.StartNew();
            await Eval($"{{const s=document.querySelector('.clip-fields select');s.value='{mode}';s.dispatchEvent(new Event('change',{{bubbles:true}}));}}");
            await Click("Найти фрагменты");
            await Wait("!document.querySelector('.clip-fields select').disabled && (document.querySelector('.clip-results') || document.querySelector('[role=alert]'))", "Clip search completed", 240);
            var project = library.Open(id);
            var selection = project.Selection;
            if (selection?.Search.Mode != mode)
                throw new InvalidOperationException("Search failed: " + await Eval("document.querySelector('[role=alert]')?.textContent"));
            if (shortRange && (selection.Search.MinSeconds != 1 || selection.Search.MaxSeconds != 6 || selection.Search.Count != 1 ||
                (mode == "structure" && selection.Clips.Length == 0)))
                throw new InvalidDataException("Short range did not yield a testable clip");
            File.WriteAllText(Path.Combine(output, mode + ".json"), JsonSerializer.Serialize(selection, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{mode}: {selection.Clips.Length} intervals, {watch.Elapsed.TotalSeconds:F2}s");
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            bool structure = mode == "structure";
            int width = structure ? 1280 : 1440, height = structure ? 800 : 900;
            double scale = structure ? 1.25 : 1.50;
            window.Width = width / dpi;
            window.Height = height / dpi;
            View.ZoomFactor = scale / dpi;
            await Wait($"Math.abs(devicePixelRatio-{scale.ToString(CultureInfo.InvariantCulture)}) < .02","Preview scale");
            await Eval("document.querySelector('.clip-results').scrollIntoView({block:'start'})");
            await Layout(mode + "-cards");
            Console.WriteLine($"Cards: {window.ActualWidth*dpi}x{window.ActualHeight*dpi}, native DPI {dpi*96}, WebView scale {scale}.");
            for (int i = 0; i < selection.Clips.Length; i++)
            {
                var clip = selection.Clips[i];
                await Eval($"{{ const v=document.querySelector('video');v.playbackRate=1;document.querySelectorAll('.clip-preview')[{i}].click();}}");
                await Wait($"!document.querySelector('video').paused && Math.abs(document.querySelector('video').currentTime-{Sec(clip.StartUs)})<1", "Preview starts at In");
                await Eval("document.querySelector('video').playbackRate=12");
                await Wait($"document.querySelector('video').paused && Math.abs(document.querySelector('video').currentTime-{Sec(clip.EndUs)})<.10", "Preview stops at Out", 35);
                Console.WriteLine($"Preview {clip.StartId}..{clip.EndId}: {Sec(clip.StartUs)}..{Sec(clip.EndUs)}s PASS");

                // Observe the player's seek events so playing the whole interval cannot pass the edge check.
                await Eval($"{{ const v=document.querySelector('video'); v.playbackRate=3; window.edgeSeeks=[]; " +
                    $"v.addEventListener('seeking',window.edgeObserver=()=>window.edgeSeeks.push(v.currentTime)); document.querySelectorAll('.clip-edges')[{i}].click(); }}");
                await Wait($"window.edgeSeeks.some(t=>Math.abs(t-{Sec(clip.StartUs)})<.25)", "Edge preview starts at In");
                if (clip.EndUs - clip.StartUs > 6_000_000)
                    await Wait($"window.edgeSeeks.some(t=>Math.abs(t-{Sec(clip.EndUs-3_000_000)})<.35)", "Edge preview seeks to final three seconds");
                await Wait($"document.querySelector('video').paused && Math.abs(document.querySelector('video').currentTime-{Sec(clip.EndUs)})<.10",
                    "Edge preview stops at Out");
                await Eval("document.querySelector('video').removeEventListener('seeking',window.edgeObserver)");
                if (clip.EndUs - clip.StartUs <= 6_000_000)
                {
                    if (await Eval($"window.edgeSeeks.some(t=>t>{Sec(clip.StartUs)}+.35 && t<{Sec(clip.EndUs)}-.35)") == "true")
                        throw new InvalidOperationException("Short edge preview skipped the middle");
                    Console.WriteLine($"Edges {clip.StartId}..{clip.EndId}: short clip played continuously to Out PASS");
                }
                else Console.WriteLine($"Edges {clip.StartId}..{clip.EndId}: In, final three seconds, Out PASS");
            }
        }
        var saved = library.Open(id).Selection;
        if (saved?.Clips.Length > 0)
        {
            await Eval("document.querySelector('.clip-actions input').click()");
            await Wait("document.querySelector('.clip-actions input')?.checked && !document.querySelector('.clip-fields select').disabled", "Marked a candidate");
            if (new Library(Path.Combine(output, "data")).Open(id).Selection!.Clips[0].State != "selected") throw new IOException("Mark was lost");
            await Click("Отклонить");
            await Wait("document.querySelector('.rejected-clips')", "Rejected clip moved to separate list");
            await Eval("document.querySelector('.rejected-clips summary').click()");
            await Click("Вернуть");
            await Wait("!document.querySelector('.rejected-clips') && !document.querySelector('.clip-fields select').disabled", "Restored candidate");
            saved = library.Open(id).Selection;
            if (saved!.Clips[0].State != "offered") throw new IOException("Rejected clip was not restored");
            Console.WriteLine("Selection, rejection and restore survived SQLite reopen.");
        }
        Environment.SetEnvironmentVariable("SHORTFORGE_OLLAMA_PORT", "1");
        await Click("Найти фрагменты");
        await Wait("document.querySelector('[role=alert]')?.textContent.includes('Ollama')", "Failed provider explained");
        if (JsonSerializer.Serialize(library.Open(id).Selection) != JsonSerializer.Serialize(saved)) throw new InvalidOperationException("Error modified saved clips");
        if (Hash(path) != before) throw new IOException("Original changed");
        Console.WriteLine($"PASS clips: SHA256 preserved, provider error retains selection, WebView2 {Web.Environment.BrowserVersionString}");
    }
}
