using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunLongVideo(string phase, string path)
    {
        var clock = Stopwatch.StartNew();
        var cache = Path.Combine(output, "data", "proxies");
        async Task OpenSaved()
        {
            await Wait("document.querySelectorAll('.project-row').length === 1", "One saved project");
            await Eval("document.querySelector('.project-row').click()");
            await Contains(Path.GetFileNameWithoutExtension(path));
        }
        if (phase == "long")
        {
            await Contains("Добавьте первое видео");
            await Eval("""
                window.stages = [];
                window.chrome.webview.addEventListener('message', ({data}) => {
                  if (data.type === 'import.stage') {
                    const {jobId,stage,percent} = data.payload;
                    window.stages.push({jobId,stage,percent,ms:performance.now()});
                  }
                });
                """);
            await DropFile(path);
            await Contains("Предпросмотр недоступен");
            Console.WriteLine($"Import completed at {clock.ElapsedMilliseconds} ms");
            await Click("Создать прокси");
            await Click("Очередь");
            const string advancing = "Number(document.querySelector('.job [role=progressbar]')?.getAttribute('aria-valuenow')) > 0";
            await Wait(advancing, "Real encoding progress in queue", 120);
            await Click("Отменить");
            await Contains("Операция отменена");
            await Contains("Активных задач нет");
            if (Directory.EnumerateFiles(cache).Any()) throw new InvalidDataException("Cancellation left a partial or published proxy.");
            Console.WriteLine($"Queue cancellation and cleanup completed at {clock.ElapsedMilliseconds} ms");
            await Click("Закрыть");
            await Click("Проекты");
            await OpenSaved();
            await Click("Создать прокси");
            await Click("Очередь");
            await Wait(advancing, "Retry reports progress from FFmpeg", 120);
            await Layout("long-proxy-progress");
            await Wait("!document.querySelector('.job')", "Encoding and validation finished", 900);
            await Contains("Активных задач нет");
            var events = await Eval("window.stages");
            File.WriteAllText(Path.Combine(output, "stages.json"), events);
            using var stages = JsonDocument.Parse(events);
            var jobs = stages.RootElement.EnumerateArray().GroupBy(e => e.GetProperty("jobId").GetString()).ToArray();
            if (jobs.Length != 3) throw new InvalidDataException("Expected import, cancellation and retry.");
            var retry = jobs[^1].ToArray();
            var percentages = retry.Where(e => e.GetProperty("stage").GetString() == "proxy" &&
                e.GetProperty("percent").ValueKind == JsonValueKind.Number).Select(e => e.GetProperty("percent").GetInt32()).ToArray();
            if (percentages.Length < 2 || !percentages.SequenceEqual(percentages.Distinct().Order()) ||
                !retry.Any(e => e.GetProperty("stage").GetString() == "proxyVerify") || percentages[^1] > 99)
                throw new InvalidDataException("Missing, regressing or premature completion progress.");
            Console.WriteLine($"Retry and verification completed at {clock.ElapsedMilliseconds} ms; {percentages.Length} distinct encoding percentages.");
            await Click("Проекты");
        }
        await OpenSaved();
        await Contains("Предпросмотр · Прокси");
        await Wait("Math.abs(document.querySelector('video')?.duration - 1800) < 0.3", "30-minute playback timeline");
        await VerifyPlayback();
        foreach (var t in new[] { 900, 1799 })
        {
            await Eval($"document.querySelector('video').currentTime={t}");
            await Wait($"!document.querySelector('video').seeking && Math.abs(document.querySelector('video').currentTime-{t}) < 0.1", "Seek");
            await Eval("void document.querySelector('video').play()");
            await Wait($"document.querySelector('video').currentTime > {t} + 0.3", "Playback after seek");
            await Eval("document.querySelector('video').pause()");
        }
        Console.WriteLine($"PASS {phase}: cached proxy decoded at start, 15:00 and 29:59.");
    }
}
