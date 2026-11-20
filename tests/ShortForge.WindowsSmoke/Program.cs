using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ShortForge.Desktop;

namespace ShortForge.WindowsSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool batch = args.Length == 3 && args[0] is "batch-crash" or "batch-restart" or "batch-sync" or "batch-selected";
        bool captions = args.Length == 3 && args[0] == "captions";
        bool autoFrame = args.Length == 3 && args[0] is "auto-motion" or "auto-cases" or "auto-restart";
        bool cuePlayback = args.Length == 3 && args[0] == "cue-playback";
        bool editor = args.Length == 3 && args[0] is "editor" or "editor-restart" or "editor-silent";
        bool clips = args.Length == 3 && args[0] == "clips";
        bool largeImport = args.Length == 3 && args[0] == "large";
        bool asr = args.Length == 3 && args[0] is "asr" or "asr-restart" or "asr-silence";
        bool phone = args.Length == 3 && args[0] is "phone" or "phone-restart";
        bool longVideo = args.Length == 3 && args[0] is "long" or "long-restart";
        bool audioTracks = args.Length == 3 && args[0] is "tracks" or "tracks-restart" or "tracks-offline-restart";
        bool checkVfr = args.Length == 4 && args[0] is "vfr" or "vfr-upgrade" or "vfr-aspect" or "vfr-aspect-restart" or "vfr-repair" or "vfr-repair-restart";
        bool hdr = args.Length == 5 && args[0] is "hdr" or "hdr-restart";
        if (!autoFrame && !batch && !captions && !cuePlayback && !editor && !clips && !asr && !phone && !largeImport && !longVideo && !audioTracks && !checkVfr && !hdr && (args.Length is not (4 or 5) || args[0] is not ("import" or "restart") ||
            (args.Length == 5 && args[4] is not ("100" or "125" or "150"))))
        {
            Console.Error.WriteLine("Usage: ShortForge.WindowsSmoke import|restart evidence-directory mp4 unsupported-mkv [native-percent]");
            Console.Error.WriteLine("   or: ShortForge.WindowsSmoke captions|cue-playback evidence-directory original-video");
            Console.Error.WriteLine("   or: ShortForge.WindowsSmoke auto-motion|auto-cases|auto-restart evidence-directory original-video");
            Console.Error.WriteLine("   or: ShortForge.WindowsSmoke batch-crash|batch-restart|batch-sync evidence-directory original-video");
            Console.Error.WriteLine("   or: ShortForge.WindowsSmoke phone|phone-restart|large|long|long-restart|tracks|tracks-restart|tracks-offline-restart evidence-directory video");
            Console.Error.WriteLine("   or: ShortForge.WindowsSmoke vfr|vfr-upgrade|vfr-aspect|vfr-aspect-restart|vfr-repair|vfr-repair-restart evidence-directory h264-mov qtrle-mov");
            Console.Error.WriteLine("   or: ShortForge.WindowsSmoke hdr|hdr-restart evidence-directory pq-mkv hlg-mp4 sdr-mkv");
            return 2;
        }
        var output = Directory.CreateDirectory(Path.GetFullPath(args[1])).FullName;
        if (args[0] == "batch-selected")
        {
            // Only this newly copied test profile is eligible for the recoverable storage failure.
            string lockPath = Path.Combine(output, "data", "export-worker.lock");
            if (Path.Exists(lockPath)) throw new IOException("Use a new copy of the selection fixture.");
            Directory.CreateDirectory(lockPath);
        }
        var app = new Application();
        string? pickerDirectory = batch ? Directory.CreateDirectory(Path.Combine(output, Smoke.BatchFolder)).FullName : Path.GetDirectoryName(Path.GetFullPath(args[2]));
        var window = new MainWindow(Path.Combine(output, "data"), pickerDirectory);
        int? nativeDpi = args.Length == 5 && !hdr ? int.Parse(args[4], CultureInfo.InvariantCulture) * 96 / 100 : null;
        var smoke = new Smoke(window, output, nativeDpi);
        var result = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                if (args[0] == "batch-selected") await smoke.RunBatchSelection();
                else if (batch) await smoke.RunBatch(args[0], Path.GetFullPath(args[2]));
                else if (autoFrame) await smoke.RunAutoFrame(args[0], Path.GetFullPath(args[2]));
                else if (captions) await smoke.RunCaptions(Path.GetFullPath(args[2]));
                else if (cuePlayback) await smoke.RunCuePlayback(Path.GetFullPath(args[2]));
                else if (editor) await smoke.RunEditor(args[0], Path.GetFullPath(args[2]));
                else if (clips) await smoke.RunClips(Path.GetFullPath(args[2]));
                else if (asr) await smoke.RunAsr(args[0], Path.GetFullPath(args[2]));
                else if (largeImport) await smoke.RunLargeImport(Path.GetFullPath(args[2]));
                else if (phone) await smoke.RunPhone(args[0], Path.GetFullPath(args[2]));
                else if (longVideo) await smoke.RunLongVideo(args[0], Path.GetFullPath(args[2]));
                else if (audioTracks) await smoke.RunAudioTracks(args[0], Path.GetFullPath(args[2]));
                else if (checkVfr) await smoke.RunVfr(args[0], Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
                else if (hdr) await smoke.RunHdr(args[0], args[2], args[3], args[4]);
                else await smoke.Run(args[0], Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
                result = 0;
            }
            catch (Exception error)
            {
                if (captions || cuePlayback)
                    Console.Error.WriteLine("Caption smoke failed: " + error.Message.Split(": ShortForge", StringSplitOptions.None)[0] + " " + error.StackTrace);
                else Console.Error.WriteLine(asr || editor ? "Media smoke failed: " + error.GetType().Name + " " + error.StackTrace : error.ToString());
                if (window.Content is TextBlock fallback) Console.Error.WriteLine(fallback.Text);
                if (!phone && !asr && !editor)
                    try { await smoke.Capture(args[0] + "-failure"); } catch { /* Report original failure. */ }
            }
            finally { window.Close(); }
        };
        app.Run(window);
        return result;
    }
}

internal sealed partial class Smoke(MainWindow window, string output, int? nativeDpi)
{
    private WebView2 View => (WebView2)window.Content;
    private CoreWebView2 Web => View.CoreWebView2;
    private Task<string> Eval(string expression) => Web.ExecuteScriptAsync(expression);
    private static string Literal(string value) => JsonSerializer.Serialize(value);

    private async Task Wait(string condition, string description, int timeoutSeconds = 25)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            if (window.Content is TextBlock fallback) throw new InvalidOperationException(fallback.Text);
            if (Web != null && await Eval($"Boolean({condition})") == "true") return;
            await Task.Delay(100);
        }
        var state = Web == null ? "WebView not initialized" : JsonSerializer.Deserialize<string>(await Eval("document.body?.innerText"));
        throw new TimeoutException(description + ": " + state);
    }

    public async Task Capture(string name)
    {
        await using var stream = File.Create(Path.Combine(output, name + ".png"));
        await Web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }

    private async Task Click(string label)
    {
        string button = $"Array.from(document.querySelectorAll('button:not(:disabled)')).find(b=>b.textContent.trim()==={Literal(label)})";
        await Wait(button, "Button " + label);
        await Eval($"{button}.scrollIntoView({{block:'center',inline:'nearest'}})");
        var point = JsonSerializer.Deserialize<double[]>(await Eval($$"""
            (() => {
              const b = {{button}}, r = b.getBoundingClientRect();
              const x = r.left + r.width / 2, y = r.top + r.height / 2;
              return b.contains(document.elementFromPoint(x,y)) ? [x,y] : null;
            })()
            """)) ?? throw new InvalidOperationException("Button is obscured: " + label);
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await Web.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type, x = point[0], y = point[1], button = "left", clickCount = 1 }));
    }

    private Task Contains(string phrase) => Wait($"document.body?.innerText.includes({Literal(phrase)})", phrase);

    private async Task Choose(string label, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var picker = Task.Run(() => Picker.Choose(hwnd, path, nativeDpi));
        await Task.WhenAll(Click(label), picker).WaitAsync(TimeSpan.FromSeconds(25));
        Console.WriteLine("Picker selected " + Path.GetFileName(path));
    }

    private async Task Layout(string name)
    {
        await Wait("document.documentElement.scrollWidth <= innerWidth + 1 && document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth + 1", "No horizontal overflow");
        await Capture(name);
    }

    private async Task VerifyPlayback()
    {
        await Wait("document.querySelector('video')?.videoWidth > 0", "Playable video metadata");
        await Eval("document.querySelector('video').currentTime = 0.7");
        await Wait("Math.abs(document.querySelector('video').currentTime - 0.7) < .08 && !document.querySelector('video').seeking", "Seek");
        await Eval("document.querySelector('video').muted = true; void document.querySelector('video').play()");
        await Wait("document.querySelector('video').currentTime > 1", "Playback advances");
        await Eval("document.querySelector('video').pause()");
        Console.WriteLine("Decoded video, seek completed, playback advanced.");
    }

    private async Task DropFile(string path)
    {
        // CDP supplies a genuine, disk-backed WebView File. The drop event exercises
        // the production React handler and the native AdditionalObjects bridge.
        await Eval("{ const i=document.createElement('input'); i.type='file'; i.id='smoke-file'; i.hidden=true; document.body.append(i) }");
        using var tree = JsonDocument.Parse(await Web.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}"));
        int rootId = tree.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
        using var field = JsonDocument.Parse(await Web.CallDevToolsProtocolMethodAsync("DOM.querySelector",
            JsonSerializer.Serialize(new { nodeId = rootId, selector = "#smoke-file" })));
        int fieldId = field.RootElement.GetProperty("nodeId").GetInt32();
        if (fieldId == 0) throw new InvalidOperationException("Test file input was not created.");
        await Web.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(new { nodeId = fieldId, files = new[] { path } }));
        await Eval("""
            {
              const input = document.querySelector('#smoke-file');
              const transfer = new DataTransfer();
              transfer.items.add(input.files[0]);
              document.querySelector('section[aria-label="Библиотека проектов"]').dispatchEvent(
                new DragEvent('drop', {dataTransfer: transfer, bubbles:true, cancelable:true}));
              input.remove();
            }
            """);
    }

    public async Task Run(string phase, string mp4, string unsupported)
    {
        bool importing = phase == "import";
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int width = importing ? 1280 : 1440, height = importing ? 800 : 900;
        var handle = new WindowInteropHelper(window).Handle;
        var actualDpi = WindowDpi.Measure(handle, nativeDpi, "WPF");
        var scale = nativeDpi.HasValue ? nativeDpi.Value / 96.0 : importing ? 1.25 : 1.50;
        window.Width = width / dpi;
        window.Height = height / dpi;
        var work = SystemParameters.WorkArea;
        window.Left = work.Left + Math.Max(0, (work.Width - window.Width) / 2);
        window.Top = work.Top + Math.Max(0, (work.Height - window.Height) / 2);
        await Wait("document.querySelector('nav button')", "WebView2 loaded the production UI");
        if (nativeDpi.HasValue)
        {
            if (Math.Abs(dpi * 96 - actualDpi) > 0.1 || View.ZoomFactor != 1)
                throw new InvalidOperationException("WPF DPI and default WebView2 zoom must match the native test mode.");
        }
        else View.ZoomFactor = scale / dpi;
        await Wait($"Math.abs(devicePixelRatio - {scale.ToString(CultureInfo.InvariantCulture)}) < 0.02", "Effective WebView2 scale");
        if (Math.Abs(window.ActualWidth * dpi - width) > 1 || Math.Abs(window.ActualHeight * dpi - height) > 1)
            throw new InvalidOperationException("The physical window size differs from the requested test size.");
        Console.WriteLine($"Phase={phase} mode={(nativeDpi.HasValue ? "native" : "webview-zoom")} PID={Environment.ProcessId} OS DPI={actualDpi} window={window.ActualWidth * dpi}x{window.ActualHeight * dpi} physical px; WebView2 zoom={View.ZoomFactor} viewport=" +
            await Eval("({width:innerWidth,height:innerHeight,dpr:devicePixelRatio})") + " WebView2=" + Web.Environment.BrowserVersionString);

        if (importing)
        {
            await Contains("Добавьте первое видео");
            await Eval("""
                const testId = crypto.randomUUID();
                window.chrome.webview.addEventListener('message', function inspect(e) {
                  if (e.data.id !== testId) return;
                  window.smokeUnknownRejected = !e.data.ok && e.data.error?.code === 'unknown_command';
                  window.chrome.webview.removeEventListener('message', inspect);
                });
                window.chrome.webview.postMessage({version:1,id:testId,type:'smoke.unknown',payload:{}});
                """);
            await Wait("window.smokeUnknownRejected", "Unknown command returned typed host error");
            await Layout("empty-1280x800");
            View.Focus();
            await Eval("document.querySelector('nav button').focus()");
            foreach (string type in new[] { "rawKeyDown", "keyUp" })
                await Web.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "Tab", code = "Tab", windowsVirtualKeyCode = 9 }));
            await Wait("document.activeElement?.title === 'Очередь' && document.activeElement.matches(':focus-visible')", "Keyboard Tab moves visible focus");
            await PressEnter();
            await Contains("Активных задач нет");
            await Layout("queue-1280x800");
            var toolSettings = Path.Combine(output, "data", "tools.json");
            // This profile was created for this run. Never replace an existing configuration.
            using (var file = new FileStream(toolSettings, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(file, Path.Combine(output, "absent-tools"));
            await Click("Настройки");
            await Contains("Не найден");
            await Layout("missing-tools-1280x800");
            await Click("Проекты");
            await Choose("Выбрать файл", mp4);
            await Wait("document.querySelector('[role=alert]')?.innerText.includes('ffprobe.exe')", "Actionable missing-tool error");
            await Contains("Добавьте первое видео");
            await Click("Открыть диагностику");
            await Contains("Не проверены");
            await Click("Включить автоматический поиск");
            await Contains("ffprobe version");
            await Contains("libx264 — доступен");
            await Contains("AAC — доступен");
            Console.WriteLine("Diagnostics opened from import error; automatic FFmpeg reports libx264 and AAC encoders.");
            if (File.Exists(toolSettings)) throw new InvalidOperationException("Custom tool selection was not reset.");
            Console.WriteLine("Recovered from missing custom folder through Settings.");

            using (var file = new FileStream(toolSettings, FileMode.CreateNew, FileAccess.Write))
                file.Write("{"u8);
            await Click("Проверить снова");
            await Contains("Сохранённую настройку папки FFmpeg не удалось прочитать");
            await Wait("Array.from(document.querySelectorAll('dd')).filter(cell => cell.textContent === 'Не проверен').length === 2", "Versions marked unverified for unreadable selection");
            await Layout("damaged-settings-1280x800");
            await Eval("Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === 'Включить автоматический поиск').focus()");
            await PressEnter();
            await Contains("ffprobe version");
            await Wait("document.activeElement?.textContent.trim() === 'Проверить снова'", "Keyboard focus after restoring automatic search");
            await Contains("ffmpeg version");
            if (File.Exists(toolSettings)) throw new InvalidOperationException("Damaged tool override survived recovery.");
            Console.WriteLine("Recovered from invalid settings JSON through Settings.");
            await Layout("settings-1280x800");
            await Click("Проекты");
            await Wait("document.querySelector('.dropzone button:enabled')", "Empty library ready");
            await Eval("document.querySelector('.dropzone button').focus()");
            var cancel = Task.Run(() => Picker.Cancel(handle, nativeDpi));
            await Task.WhenAll(PressEnter(), cancel).WaitAsync(TimeSpan.FromSeconds(25));
            await Wait("document.activeElement === document.querySelector('.dropzone button:enabled') && document.activeElement.matches(':focus-visible')",
                "Keyboard focus returned to the import button after cancellation");
            await Wait("!document.querySelector('[role=alert],.job,.project-row')", "Cancellation left the library empty");
            var retry = Task.Run(() => Picker.Choose(handle, mp4, nativeDpi));
            await Task.WhenAll(PressEnter(), retry).WaitAsync(TimeSpan.FromSeconds(25));
            Console.WriteLine("Native picker cancelled; Enter on the restored button reopened it and selected the fixture.");
        }
        else
        {
            await Wait("document.querySelectorAll('.project-row').length === 2", "Two saved projects after process restart");
            await Wait("Array.from(document.querySelectorAll('.thumb img')).length === 2 && Array.from(document.querySelectorAll('.thumb img')).every(i=>i.naturalWidth > 0)", "Persisted thumbnails");
            await Layout("library-1440x900");
            var title = Literal(Path.GetFileNameWithoutExtension(mp4));
            await Eval($"Array.from(document.querySelectorAll('.project-row')).find(b=>b.textContent.includes({title})).click()");
        }
        await Contains(Path.GetFileNameWithoutExtension(mp4));
        await Contains("320 × 180");
        await VerifyPlayback();
        await Eval("""
            void fetch(document.querySelector('video').currentSrc, {headers:{Range:'bytes=4-7'}})
              .then(async r => {
                const bytes = new Uint8Array(await r.arrayBuffer());
                window.smokeRange = r.status === 206 && /^bytes 4-7\//.test(r.headers.get('Content-Range')) &&
                  String.fromCharCode(...bytes) === 'ftyp';
              });
            """);
        await Wait("window.smokeRange", "Four requested bytes from MP4 header");
        await Layout("workspace-" + (importing ? "1280x800" : "1440x900"));

        if (importing)
        {
            await Click("Все проекты");
            var broken = Path.Combine(Path.GetDirectoryName(mp4)!, "broken.mp4");
            File.WriteAllBytes(broken, [0, 12, 2, 1]);
            await Choose("Добавить видео", broken);
            await Wait("document.querySelector('[role=alert]')", "Corrupt source error");
            await Layout("invalid-1280x800");
            await Click("Закрыть");
            await Choose("Добавить видео", unsupported);
            await Contains("В видео нет аудиодорожки");
            await Contains("Предпросмотр недоступен");
            await Layout("proxy-needed-1280x800");
            await Click("Создать прокси");
            await Contains("Предпросмотр · Прокси");
            await VerifyPlayback();
            await Layout("proxy-1280x800");
        }
        else
        {
            await Eval("document.querySelector('button.back').focus()");
            await PressEnter();
            await Wait($"document.activeElement?.classList.contains('project-row') && document.activeElement.textContent.includes({Literal(Path.GetFileNameWithoutExtension(mp4))})", "Focus returned to the reopened project");
            await Wait("Array.from(document.querySelectorAll('.project-row')).filter(b => b.textContent.includes('Исходник не найден')).length === 1",
                "Only the renamed fixture needs relinking");
            await Eval("Array.from(document.querySelectorAll('.project-row')).find(b => b.textContent.includes('Исходник не найден')).click()");
            await Contains("Сохранённый прокси доступен для просмотра");
            await Wait("document.querySelector('video')?.currentSrc.includes('/proxy/')", "Offline preview uses cached proxy");
            await VerifyPlayback();
            await Layout("offline-proxy-1440x900");
            await Choose("Найти файл", mp4);
            await Wait("document.querySelector('[role=alert]')?.innerText.includes('отличается')", "Unrelated source rejected");
            await Contains("Сохранённый прокси доступен для просмотра");
            await Choose("Найти файл", unsupported);
            await Wait("!document.querySelector('.missing') && !document.querySelector('[role=alert]')", "Relink restored original");
            await Contains("Предпросмотр · Прокси");
            await VerifyPlayback();
            await Click("Все проекты");
            await Wait("document.querySelectorAll('.project-row').length === 2 && !document.querySelector('.project-row .warning')",
                "Relinking preserved both library entries");
            Console.WriteLine("Moved original: cached proxy played offline, unrelated file rejected, verified original relinked.");
            var folder = Path.GetDirectoryName(mp4)!;
            string longName = string.Join(" ", Enumerable.Repeat("Съёмка продолжительного разговора", 4));
            var longPath = Path.Combine(folder, longName + ".mkv");
            File.Copy(Path.Combine(folder, "тихий кадр.mkv"), longPath);
            using var source = File.OpenRead(longPath);
            var digest = SHA256.HashData(source);
            await DropFile(longPath);
            await Contains(longName);
            await Contains("160 × 120");
            await Contains("В видео нет аудиодорожки");
            await Layout("drop-1440x900");
            source.Position = 0;
            if (!SHA256.HashData(source).SequenceEqual(digest)) throw new InvalidDataException("Dropped source changed.");
            Console.WriteLine($"Real File object imported via WebView2 drop handler, path length {longPath.Length}.");
        }
        WindowDpi.Measure(handle, nativeDpi, "WPF");
        Console.WriteLine("PASS " + phase);
    }

    private async Task PressEnter()
    {
        await Web.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            JsonSerializer.Serialize(new { type = "keyDown", key = "Enter", code = "Enter", text = "\r", windowsVirtualKeyCode = 13 }));
        await Web.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            JsonSerializer.Serialize(new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 }));
    }
}
