using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Interop;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunCaptions(string source)
    {
        const string shortText = "До правки";
        const string correctedText = @"Бенну ə ı ş ğ ö ü {\b1} <кадр> 🌙";
        const string video = "document.querySelector('video.caption-rendered')";
        string data = Path.Combine(output, "data");
        var library = new Library(data);
        var project = library.Open(library.List().Single().Id);
        if (!project.SourceAvailable || !Path.GetFullPath(project.OriginalPath).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The isolated project must point to the supplied speech video.");
        var clip = project.Edits.Single(e => e.Title == "Бенну · перед закрытием");
        EditorClip Read() => library.OpenEdit(project.Id, clip.Id);
        var original = Read();
        if (original.Cues.Single().StartUs != 0 || original.Edit.EndUs - original.Edit.StartUs != 1_250_000)
            throw new InvalidDataException("Expected the trimmed P05 speech fixture.");
        var transcript = JsonSerializer.Serialize(project.Transcript);
        var sourceHash = HashFile(source);

        async Task Change(string selector, string value, int index = 0)
        {
            int previous = Read().Revision;
            await Eval($$"""
                { const el=document.querySelectorAll({{Literal(selector)}})[{{index}}]; el.focus();
                  const proto=el.tagName==='SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
                  Object.getOwnPropertyDescriptor(proto,'value').set.call(el,{{Literal(value)}});
                  const event=el.tagName==='SELECT' ? 'change' : 'input';
                  el.dispatchEvent(new Event(event,{bubbles:true}));
                  if(el.type==='color') el.dispatchEvent(new Event('change',{bubbles:true}));
                  el.blur(); }
                """);
            var watch = Stopwatch.StartNew();
            while (Read().Revision == previous && watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(60);
            if (Read().Revision <= previous) throw new InvalidDataException("Field was not saved: " + selector);
            await Wait("document.querySelector('dialog header [role=status]')?.textContent.includes('Сохранено')", "Autosave");
        }
        async Task<string> Render(string evidenceName)
        {
            var cache = Path.Combine(data, "caption-previews");
            var before = Directory.Exists(cache) ? Directory.GetFiles(cache, "*.mp4") : [];
            await Click("Точный предпросмотр");
            await Wait($"{video}?.videoWidth > 0 && {video}.duration > 1", "Rendered MP4 opened in WebView2", 90);
            var uri = new Uri(JsonSerializer.Deserialize<string>(await Eval($"{video}.currentSrc"))!);
            if (uri.AbsolutePath.Split('/') is not ["", "caption-preview", var last] || !Guid.TryParse(last, out _))
                throw new InvalidDataException("Preview did not use an opaque media URL.");
            var path = Directory.GetFiles(cache, "*.mp4").Except(before, StringComparer.OrdinalIgnoreCase).Single();
            File.Copy(path, Path.Combine(output, evidenceName + ".mp4"));
            await Eval($"{{const v={video}; v.muted=true;v.currentTime=0;void v.play();}}");
            await Wait($"{video}.currentTime > .25", "Decoded preview playback advanced");
            await Eval($"{video}.pause()");
            Console.WriteLine($"PASS {evidenceName}: revision {Read().Revision}, preview playback and verified cache file copied.");
            return uri.AbsoluteUri;
        }

        await Wait("document.querySelector('.project-row')", "Existing speech project");
        await Eval("document.querySelector('.project-row').click()");
        await Click(clip.Title);
        await Wait("document.querySelector('#caption-render')", "Caption editor loaded");
        await Change("#caption-preset", "contrast");
        await Change("#caption-color", "#00ff00");
        if (Read().Edit.Style.Color != "#00FF00") throw new InvalidDataException("Selected colour did not persist.");
        await Change(".cue-timing input[type=number]", "0.250");
        await Change(".cue-timing input[type=number]", "1.100", 1);
        await Change(".edit-cues input[type=text]", shortText);
        if (Read().Cues.Single() is not { StartUs: 250_000, EndUs: 1_100_000, Text: shortText })
            throw new InvalidDataException("Clip-relative edits did not persist.");

        // Dispatch cancellation before completion and retry through the same UI.
        await Click("Точный предпросмотр");
        await Click("Отменить предпросмотр");
        await Wait($"!{video} && document.querySelector('#caption-render:not(:disabled)')", "Cancelled preview ready for retry");
        var cache = Path.Combine(data, "caption-previews");
        if (Directory.Exists(cache) && Directory.GetFiles(cache, "*.mp4").Length != 0)
            throw new InvalidDataException("Cancellation left a published preview.");
        Console.WriteLine("PASS preview cancelled from editor without a published MP4.");

        var firstUrl = await Render("first-edit");
        await Change(".edit-cues input[type=text]", correctedText);
        await Wait($"!{video}", "Text edit retired previous preview");
        await Eval($"window.retiredPreviewStatus=0; void fetch({Literal(firstUrl)},{{method:'HEAD'}}).then(r=>window.retiredPreviewStatus=r.status)");
        await Wait("window.retiredPreviewStatus===404", "Host revoked old preview URL");
        if (Read().Cues.Single().Text != correctedText) throw new InvalidDataException("Corrected text changed in storage.");
        var secondUrl = await Render("second-edit");
        if (firstUrl == secondUrl) throw new InvalidDataException("New text reused the old media URL.");

        string srt = Path.Combine(output, "Титры O'Brien.srt");
        var owner = new WindowInteropHelper(window).Handle;
        async Task SaveDialog(string buttonId, string title, string? path)
        {
            await Wait($"document.querySelector('#{buttonId}:not(:disabled)')", "Native save command available");
            var buttonLabel = JsonSerializer.Deserialize<string>(await Eval($"document.getElementById('{buttonId}').textContent.trim()"))!;
            var picker = Task.Run(() => CaptionSaveDialog.Complete(owner, title, path));
            await Task.WhenAll(Click(buttonLabel), picker).WaitAsync(TimeSpan.FromSeconds(25));
            await Wait($"document.querySelector('#{buttonId}:not(:disabled)')", "Native save command completed", 90);
            if (path != null && !File.Exists(path)) throw new IOException("Requested output did not appear.");
        }
        await SaveDialog("caption-srt", "Сохранить субтитры SRT", null);
        if (File.Exists(srt)) throw new InvalidDataException("Cancelled SRT file appeared.");
        await SaveDialog("caption-srt", "Сохранить субтитры SRT", srt);
        string sidecar = WebUtility.HtmlDecode(File.ReadAllText(srt));
        if (!sidecar.Contains("00:00:00,250", StringComparison.Ordinal) ||
            !sidecar.Contains("00:00:01,100", StringComparison.Ordinal) ||
            !Regex.Replace(sidecar, @"\s+", " ").Contains(correctedText, StringComparison.Ordinal))
            throw new InvalidDataException("SRT does not contain the saved local timing and literal cue text.");
        await SaveDialog("caption-video", "Сохранить клип MP4", Path.Combine(output, "saved-clip.mp4"));

        if (JsonSerializer.Serialize(library.Open(project.Id).Transcript) != transcript || !sourceHash.SequenceEqual(HashFile(source)))
            throw new InvalidDataException("Original transcript or source video changed.");
        await Click("К проекту");
        await Click(clip.Title);
        await Wait($"document.querySelector('.edit-cues input[type=text]')?.value === {Literal(correctedText)} && document.querySelector('#caption-color')?.value === '#00ff00'", "Saved caption settings and text reopened");
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
        foreach (var (width, height, zoom) in new[] { (1280, 800, 1.25), (1440, 900, 1.50) })
        {
            window.Width = width / dpi;
            window.Height = height / dpi;
            View.ZoomFactor = zoom / dpi;
            await Wait($"Math.abs(devicePixelRatio-{zoom.ToString(CultureInfo.InvariantCulture)})<.02 && document.querySelector('dialog').scrollWidth <= document.querySelector('dialog').clientWidth+1", "Caption editor fits");
            await Layout("captions-" + width);
            Console.WriteLine($"PASS {width}x{height}, native DPI {dpi * 96}, WebView zoom {zoom}.");
        }
        Console.WriteLine("PASS native SRT save/cancel and MP4 save, local timing and mixed-script cue text, reopened editor and untouched source/ASR.");
    }

    private static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}

internal static class CaptionSaveDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    public static void Complete(IntPtr owner, string title, string? path)
    {
        IntPtr dialog = IntPtr.Zero;
        if (!SpinWait.SpinUntil(() =>
        {
            dialog = FindWindowEx(IntPtr.Zero, dialog, "#32770", title);
            return dialog != IntPtr.Zero && GetWindow(dialog, 4 /* GW_OWNER */) == owner;
        }, TimeSpan.FromSeconds(12))) throw new TimeoutException("Expected a Save dialog owned by the smoke window.");
        try
        {
            if (path == null) { PostMessage(dialog, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); return; }
            var root = AutomationElement.FromHandle(dialog);
            var defaultName = Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase) ? "Субтитры.srt" : "Клип.mp4";
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            ValuePattern? value = null;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8) && value == null)
            {
                foreach (AutomationElement field in root.FindAll(TreeScope.Descendants, condition))
                    if (field.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern v &&
                        !v.Current.IsReadOnly && v.Current.Value.Equals(defaultName, StringComparison.OrdinalIgnoreCase))
                    { value = v; break; }
                if (value == null) Thread.Sleep(100);
            }
            if (value == null) throw new InvalidOperationException("Native filename field with the expected default name missing.");
            if (File.Exists(path)) throw new IOException("Evidence file already exists.");
            value.SetValue(path);
            if (value.Current.Value != path) throw new InvalidDataException("Native SaveFileDialog did not accept the full evidence path.");
            var save = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1")));
            ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }
        catch
        {
            PostMessage(dialog, 0x10, IntPtr.Zero, IntPtr.Zero);
            throw;
        }
        finally
        {
            if (!SpinWait.SpinUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Native Save dialog did not close.");
        }
    }
}
