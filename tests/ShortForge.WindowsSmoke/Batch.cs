using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media;
using ShortForge.Core;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    internal const string BatchFolder = "Экспорт O'Brien";
    private const string BatchPanel = "section[aria-label='Пакетный экспорт']";
    private const string Rows = "li[data-job-id]";

    public async Task RunBatch(string phase, string source)
    {
        var library = new Library(Path.Combine(output, "data"));
        string folder = Path.Combine(output, BatchFolder);
        var digest = HashFile(source);
        if (phase == "batch-sync")
        {
            string bin = Environment.GetEnvironmentVariable("SHORTFORGE_FFMPEG_BIN")!;
            string probe = Path.Combine(bin, "ffprobe.exe"), encoder = Path.Combine(bin, "ffmpeg.exe");
            // Track 0 contains timed beeps. The real saved preview uses silent track 1.
            var imported = await library.ImportAsync(source, probe, encoder, CancellationToken.None);
            var clip = library.CreateEdit(imported.Id);
            clip = library.SaveEdit(imported.Id, clip.Id, clip.Revision, clip.Edit with {
                Title = "Метки синхронизации", StartUs = 400_000, EndUs = 5_600_000
            }, null);
            var proxy = await library.CreateProxyAsync(imported.Id, probe, encoder, CancellationToken.None, audioTrack: 1);
            if (proxy.ProxyPath == null || proxy.ProxyAudioTrack != 1 || clip.AudioTrack != 0)
                throw new InvalidDataException("Expected a silent preview and audible source clip.");
            await Wait("document.querySelector('nav button')", "Production UI");
            Web.Reload();
            await OpenBatchProject();
            await Wait("document.querySelector('video')?.currentSrc.includes('/proxy/')", "Active proxy URL");
            await VerifyPlayback();
            await StartBatch([clip.Edit.Title], sidecars: false);
            await WaitForRows("completed", 1);
            AssertSameSource();
            Console.WriteLine("PASS WPF played the real cached track 1 proxy then exported track 0 of the original.");
            return;
        }

        var project = library.Open(library.List().Single().Id);
        if (!string.Equals(project.OriginalPath, source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Copied SQLite must reference the supplied NASA video.");
        string expectedFile = Path.Combine(output, "expected.json");
        string[] titles = ["Пакет первый", "Пакет второй", "Пакет третий"];
        if (phase == "batch-crash")
        {
            var clips = new List<EditorClip>();
            long[] starts = [2_000_000, 7_500_000, 11_400_000];
            long[] lengths = [8_000_000, 3_000_000, 4_200_000];
            for (int i = 0; i < titles.Length; i++)
            {
                var clip = library.CreateEdit(project.Id);
                var edit = clip.Edit with { Title = titles[i], StartUs = starts[i], EndUs = starts[i] + lengths[i],
                    CaptionStyle = CaptionStyle.Neutral with { Color = "#00FF00" } };
                clip = library.SaveEdit(project.Id, clip.Id, clip.Revision, edit, null);
                var cue = clip.Cues[0];
                clip = library.SaveEdit(project.Id, clip.Id, clip.Revision, edit,
                    new(cue.Index, $"Правка {i + 1} · Бенну", starts[i] + 250_000, starts[i] + 1_250_000));
                clips.Add(clip);
            }
            File.WriteAllText(expectedFile, JsonSerializer.Serialize(clips.Select(c => new {
                title = c.Edit.Title, startUs = c.Edit.StartUs, endUs = c.Edit.EndUs,
                cues = c.Cues.Select(q => new {startUs = q.StartUs, endUs = q.EndUs, text = q.Text})
            })));
            await OpenBatchProject();
            await BatchLayout(1280, 800, 1.25, "batch-select");
            await StartBatch(titles, sidecars: true);
            await WaitForRows("running", 1);
            await WaitForStaging(folder);
            await WaitForRows("queued", 2);
            Console.WriteLine("PASS three selected; one running inside FFmpeg with a staged file, two queued. Forcing termination of this smoke host.");
            File.WriteAllText(Path.Combine(output, "crash-marker.txt"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Console.Out.Flush();
            Process.GetCurrentProcess().Kill();
            return;
        }

        await NavigateBatch("Очередь");
        await WaitForRows("interrupted", 1);
        await ClickRow("interrupted", "Повторить");
        await WaitForRows("completed", 3);
        await BatchLayout(1440, 900, 1.50, "batch-completed");
        var completedFiles = Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories);
        if (completedFiles.Length != 3)
            throw new InvalidDataException("Interrupted encode left a staged file, or a completed video is missing.");
        Console.WriteLine("PASS restart recovered the interrupted job; retry plus two surviving jobs produced three MP4s.");
        // Give the real queue enough encoding work to observe and cancel on fast machines.
        // Very short clips can finish between the production queue's one-second refreshes.
        const string cancelTitle = "Проверка отмены";
        var cancelClip = library.CreateEdit(project.Id);
        library.SaveEdit(project.Id, cancelClip.Id, cancelClip.Revision, cancelClip.Edit with {
            Title = cancelTitle, StartUs = 0, EndUs = Math.Min(90_000_000, project.DurationUs)
        }, null);
        await NavigateBatch("Проекты");
        await OpenBatchProject();
        await StartBatch([cancelTitle], sidecars: true);
        await WaitForRows("running", 1);
        await WaitForStaging(folder);
        await ClickRow("running", "Отменить");
        await WaitForRows("cancelled", 1);
        await WaitForRows("completed", 3);
        if (!Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(completedFiles))
            throw new InvalidDataException("Cancellation damaged previous exports or left an extra MP4.");
        AssertSameSource();
        Console.WriteLine("PASS active FFmpeg cancelled, three validated videos retained, source unchanged.");

        void AssertSameSource()
        {
            if (!digest.SequenceEqual(HashFile(source))) throw new InvalidDataException("Source hash changed.");
        }
    }

    private async Task OpenBatchProject()
    {
        await Wait("document.querySelector('.project-row')", "Project in isolated library");
        await Eval("document.querySelector('.project-row').click()");
        await Wait($"document.querySelector({Literal(BatchPanel)})", "Production batch controls");
    }

    private async Task NavigateBatch(string title)
    {
        string selector = "nav button[title='" + title + "']";
        await Wait($"document.querySelector({Literal(selector)})", "Workspace navigation");
        await Eval($"document.querySelector({Literal(selector)}).click()");
        await Wait($"document.querySelector({Literal(selector)})?.getAttribute('aria-current')==='page'", "Opened " + title);
    }

    private async Task StartBatch(string[] titles, bool sidecars)
    {
        string[] checkedNames = sidecars ? [..titles, "Сохранить SRT", "Сохранить манифест JSON"] : titles;
        // Leave quality options at their defaults. All existing edits and candidate groups
        // outside this exact selection should be excluded, including preselected items.
        var selected = await Eval($$"""
            (() => {
              const panel = document.querySelector({{Literal(BatchPanel)}});
              const desired = {{JsonSerializer.Serialize(checkedNames)}};
              const found = [];
              for (const box of panel.querySelectorAll('input[type=checkbox]')) {
                const name = box.getAttribute('aria-label') || [...box.labels].map(l => l.textContent.trim()).join(' ');
                if (/громкост|NVENC/i.test(name)) continue;
                const want = desired.includes(name);
                if (box.checked !== want) box.click();
                if (want && box.checked) found.push(name);
              }
              return found;
            })()
            """);
        var names = JsonSerializer.Deserialize<string[]>(selected) ?? [];
        if (!names.ToHashSet().SetEquals(checkedNames))
            throw new InvalidDataException($"Expected export checkboxes missing: {string.Join(", ", checkedNames.Except(names))}.");
        var owner = new WindowInteropHelper(window).Handle;
        var picker = Task.Run(() => Picker.AcceptFolder(owner, Path.Combine(output, BatchFolder)));
        await Task.WhenAll(Click("Выбрать папку и экспортировать"), picker).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private Task WaitForRows(string state, int count) =>
        Wait($"document.querySelectorAll(\"{Rows}[data-state='{state}']\").length==={count}", $"Queue: {count} {state}", 100);

    private async Task ClickRow(string state, string action)
    {
        string button = $"Array.from(document.querySelectorAll(\"{Rows}[data-state='{state}'] button:enabled\")).find(b=>b.textContent.trim()==={Literal(action)})";
        await Wait(button, "Available queue action " + action);
        await Eval(button + ".click()");
    }

    private static async Task WaitForStaging(string folder)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(25))
        {
            if (Directory.EnumerateFiles(folder, "render.partial", SearchOption.AllDirectories).Any()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("No in-progress MP4 found in the selected export folder.");
    }

    private async Task BatchLayout(int width, int height, double scale, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        window.Width = width / dpi; window.Height = height / dpi; View.ZoomFactor = scale / dpi;
        await Wait($"Math.abs(devicePixelRatio-{scale.ToString(CultureInfo.InvariantCulture)}) < .02", "WebView display scale");
        await Layout(name);
        Console.WriteLine($"PASS {width}x{height}, native {dpi * 96} DPI, WebView scale {scale}.");
    }
}
