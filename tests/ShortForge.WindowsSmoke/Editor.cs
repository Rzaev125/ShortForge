using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunEditor(string phase, string source)
    {
        const string title = "Монтаж · Бенну";
        const string closingTitle = "Бенну · перед закрытием";
        const string correction = "Проверка ə/ı/ş <кадр> {текст}";
        static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
        async Task Field(string selector, string value, bool blur = true)
        {
            await Eval($$"""
                { const el=document.querySelector({{Literal(selector)}}); el.focus();
                  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,{{Literal(value)}});
                  el.dispatchEvent(new Event('input',{bubbles:true})); if ({{(blur ? "true" : "false")}}) el.blur(); }
                """);
        }
        async Task Drag(string selector, double from, double to)
        {
            var coordinates=JsonSerializer.Deserialize<double[]>(await Eval($$"""
                (()=>{const el=document.querySelector({{Literal(selector)}});el.scrollIntoView({block:'center'});
                  const r=el.getBoundingClientRect();return [r.left,r.top+r.height/2,r.width]})()
                """))!;
            async Task Mouse(string type,double at,int buttons) => await Web.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type, x=coordinates[0]+at*coordinates[2],y=coordinates[1],button="left",buttons,clickCount=1 }));
            await Mouse("mousePressed",from,1);
            await Mouse("mouseMoved",to,1);
            await Mouse("mouseReleased",to,0);
        }
        async Task Saved(Func<bool> condition)
        {
            for (int i=0;i<120;i++) { if(condition()) return; await Task.Delay(50); }
            throw new InvalidOperationException("Expected database edit was not saved: " + await Eval("document.querySelector('dialog')?.innerText"));
        }
        using var stream = File.OpenRead(source);
        var before = SHA256.HashData(stream);
        var db = new Library(Path.Combine(output,"data"));
        if (phase == "editor-silent")
        {
            await Contains("Добавьте первое видео");
            await DropFile(source);
            await Click("Создать клип вручную");
            await Contains("В этом интервале нет распознанных фраз.");
            var silent = db.Open(db.List().Single().Id);
            Check(silent.AudioTracks == 0 && db.OpenEdit(silent.Id,silent.Edits.Single().Id).Cues.Length == 0,"Silent manual clip");
            Console.WriteLine("PASS manual clip from video without audio or transcript");
            return;
        }

        await Wait("document.querySelector('.project-row')","Saved project");
        await Eval("document.querySelector('.project-row').click()");
        await Contains("Создать клип вручную");
        var project = db.Open(db.List().Single().Id);
        var raw = JsonSerializer.Serialize(project.Transcript);
        var clipId = phase=="editor-restart" ? project.Edits.Single(e=>e.Title==closingTitle).Id : Guid.Empty;
        EditorClip Read() => db.OpenEdit(project.Id,clipId);
        if (phase == "editor")
        {
            await Click("Создать клип вручную");
            await Wait("document.querySelector('dialog[open]')","Manual editor");
            await Click("К проекту");
            if (!project.Selection!.Clips.Any(c=>c.State=="selected"))
            {
                await Eval("document.querySelector('.clip-results li input[type=checkbox]').click()");
                await Wait("document.querySelector('.clip-results input:checked') && !document.querySelector('.clip-results input:checked').disabled","Selected");
            }
            await Click("Открыть в редакторе");
            await Wait("document.querySelector('dialog video')?.videoWidth>0","Editor has playable picture");
            var selected = db.Open(project.Id).Edits;
            Check(selected.Length==2,"Independent manual and candidate clips");
            clipId = selected.Last().Id;
            var original = Read();
            var cue = original.Cues.First();
            long start = original.Edit.StartUs+cue.StartUs+100000;
            long end = Math.Min(original.Edit.StartUs+cue.EndUs,start+1250000);
            Check(end-start>250000,"Fixture needs a long enough first phrase");
            await Field(".edit-props input[type=text]",title,false);
            await Saved(()=>Read().Edit.Title==title);
            await Field(".edit-times label:first-child input",(start/1e6).ToString("R",CultureInfo.InvariantCulture));
            await Saved(()=>Read().Edit.StartUs==start);
            await Field(".edit-times label:last-child input",(end/1e6).ToString("R",CultureInfo.InvariantCulture));
            await Saved(()=>Read().Edit.EndUs==end);
            Check(Read().Cues.Single() is {StartUs:0} clipped && clipped.EndUs==end-start,"C05 clip-relative half-open intersection");
            await Field(".edit-times label:first-child input","-1");
            await Contains("Out должен быть позже In");
            Check(Read().Edit.StartUs==start,"Invalid input not persisted");
            await Field(".edit-cues input[type=text]",correction);
            await Saved(()=>Read().Cues.Single().Text==correction);
            await Click("Отменить правку");
            await Saved(()=>Read().Cues.Single().Text==cue.Text);
            await Click("Повторить правку");
            await Saved(()=>Read().Cues.Single().Text==correction);
            await Eval("""
                { const el=document.querySelector('.edit-props input[type=range]');
                  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,'0.24');
                  el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new KeyboardEvent('keyup',{key:'ArrowLeft',bubbles:true})); }
                """);
            await Saved(()=>Read().Edit.X==.24);
            await Wait("getComputedStyle(document.querySelector('dialog video')).objectPosition.startsWith('24%')","Normalized crop visible");
            await Click("Вписать");
            await Saved(()=>Read().Edit.Frame=="fit");
            await Wait("getComputedStyle(document.querySelector('dialog video')).objectFit==='contain'","Fit with black background");
            await Click("Заполнить");
            await Saved(()=>Read().Edit.Frame=="crop");
            await Eval("const z=document.querySelector('.edit-timeline select');z.value='16';z.dispatchEvent(new Event('change',{bubbles:true}));");
            await Wait("Number(document.querySelector('[aria-label=\"Граница Out\"]').max)<10000000","Zoomed source timeline");
            await Drag("[aria-label='Граница Out']",.22,.55);
            await Saved(()=>Read().Edit.EndUs>end+1000000);
            await Click("Отменить правку");
            await Saved(()=>Read().Edit.EndUs==end);
            await Drag(".edit-frame",.5,.75);
            await Saved(()=>Read().Edit.X<.24);
            await Click("Отменить правку");
            await Saved(()=>Read().Edit.X==.24);
            Console.WriteLine("PASS real mouse drags on zoomed Out handle and vertical frame, undo restored both");
            await Click("Воспроизвести клип");
            await Wait("!document.querySelector('dialog video').paused","Real clipped playback");
            await Wait($"document.querySelector('dialog video').paused && Math.abs(document.querySelector('dialog video').currentTime-{(end/1e6).ToString("R",CultureInfo.InvariantCulture)})<.04","Playback stops at new Out");
            var neighbor=db.OpenEdit(project.Id,selected.First().Id);
            Check(neighbor.Revision==1 && neighbor.Edit.Title=="Новый клип","Undo/redo isolated");
            Check(JsonSerializer.Serialize(db.Open(project.Id).Transcript)==raw,"ASR changed");
            var saved = Read();
            var expected = saved with { Revision=saved.Revision+1,Edit=saved.Edit with {Title=closingTitle} };
            File.WriteAllText(Path.Combine(output,"saved-editor.json"),JsonSerializer.Serialize(expected));
            File.WriteAllBytes(Path.Combine(output,"transcript.sha256"),SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
            Console.WriteLine($"PASS candidate + manual, idle autosave, numeric In/Out, invalid trim, cue trim + undo/redo, crop 0.24, fit, original ASR and neighbor. Real playback {start}..{end} us.");
        }
        else
        {
            Check(project.Edits.Length==2 && project.Selection!.Clips.Any(c=>c.State=="selected"),"Restored candidate selection and editable list");
            Check(JsonSerializer.Serialize(Read())==File.ReadAllText(Path.Combine(output,"saved-editor.json")),"Restart changed edit");
            Check(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)).SequenceEqual(File.ReadAllBytes(Path.Combine(output,"transcript.sha256"))),"Restart changed ASR");
            await Click(closingTitle);
            await Wait($"document.querySelector('.edit-cues input[type=text]')?.value==={Literal(correction)}","Edited cue after restart");
            Console.WriteLine("PASS separate WPF process restored selection, manual neighbor, title, normalized frame, trimmed cues, original ASR and bounds");
        }
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
        foreach(var (width,height,scale) in new[]{(1280,800,1.25),(1440,900,1.50)})
        {
            window.Width=width/dpi; window.Height=height/dpi; View.ZoomFactor=scale/dpi;
            await Wait($"Math.abs(devicePixelRatio-{scale.ToString(CultureInfo.InvariantCulture)})<.02 && document.querySelector('dialog').scrollWidth <= document.querySelector('dialog').clientWidth+1","Editor fits width");
            await Layout(phase+"-"+width);
            Console.WriteLine($"PASS {width}x{height}, native DPI {dpi*96}, WebView zoom {scale}");
        }
        stream.Position=0;
        Check(before.SequenceEqual(SHA256.HashData(stream)),"Source hash changed");
        if (phase == "editor")
        {
            await Field(".edit-props input[type=text]",closingTitle,false);
            // Main closes the real WPF window as soon as the keystroke has been dispatched.
        }
    }
}
