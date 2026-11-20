using System.Globalization;
using System.IO;
using System.Text.Json;
using ShortForge.Infrastructure;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunCuePlayback(string source)
    {
        var library = new Library(Path.Combine(output, "data"));
        var project = library.Open(library.List().Single().Id);
        var clip = project.Edits.Single(e => e.Title == "Бенну · перед закрытием");
        if (!project.SourceAvailable || !string.Equals(source, project.OriginalPath, StringComparison.OrdinalIgnoreCase) ||
            library.OpenEdit(project.Id, clip.Id).Edit.StartUs != 130_000)
            throw new InvalidDataException("Expected the saved P05 speech clip with a nonzero In.");
        var original = HashFile(source);
        var transcript = JsonSerializer.Serialize(project.Transcript);
        await Wait("document.querySelector('.project-row')", "Saved speech project");
        await Eval("document.querySelector('.project-row').click()");
        await Click(clip.Title);
        await Wait("document.querySelector('.edit-frame video')?.readyState >= 2", "Editor media loaded");
        await Eval("""
            window.v = document.querySelector('.edit-frame video');
            window.events = [];
            for (const type of ['seeked','playing','pause','timeupdate'])
              v.addEventListener(type, () => events.push({type, t:v.currentTime, paused:v.paused}));
            """);
        async Task Field(string selector, string value)
        {
            await Eval($$"""
                { const el=document.querySelector({{Literal(selector)}}); el.focus();
                  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,{{Literal(value)}});
                  el.dispatchEvent(new Event('input',{bubbles:true})); }
                """);
        }
        const string start = ".cue-timing label:first-child input";
        const string end = ".cue-timing label:last-child input";
        async Task Audition(double from, double until)
        {
            var a = from.ToString("R", CultureInfo.InvariantCulture);
            var b = until.ToString("R", CultureInfo.InvariantCulture);
            await Eval("events.length=0");
            await Click("Прослушать фразу");
            await Wait($"events.some(e=>e.type==='seeked' && Math.abs(e.t-{a})<.04) && " +
                $"!v.muted && !v.paused && v.currentTime>{a}+.10 && v.currentTime<{b}", "Unmuted media advances inside edited cue");
            await Wait($"v.paused && !v.seeking && Math.abs(v.currentTime-{b})<.002", "Playback stopped at cue Out");
            Console.WriteLine($"PASS source {a}..{b} s: " + await Eval("events.filter(e=>e.type!=='timeupdate')"));
        }
        // Focusing the button commits the currently typed draft. Audition must use that saved revision.
        await Field(start, "0.25");
        await Field(end, "1.10");
        await Audition(.38, 1.23);
        await Field(end, "0.85");
        await Audition(.38, .98);
        await Field(".edit-times label:first-child input", "0.20");
        await Audition(.38, .98);
        if (library.OpenEdit(project.Id, clip.Id).Cues.Single() is not { StartUs: 180_000, EndUs: 780_000 })
            throw new InvalidDataException("Expected revised cue times relative to the trimmed clip.");

        await Click("Прослушать фразу");
        await Wait("!v.paused && v.currentTime>.46 && v.currentTime<.8", "Playback before manual stop");
        await Click("Остановить фразу");
        await Wait("v.paused && v.currentTime<.98", "Manual stop before cue Out");
        await Click("Воспроизвести клип");
        await Wait("!v.paused && v.currentTime>1.04", "Full clip continues past previous cue Out");
        await Wait("v.paused && Math.abs(v.currentTime-1.38)<.002", "Clip still stops at Out");
        await Click("Прослушать фразу");
        await Wait("!v.paused", "Playback before seek");
        await Field("[aria-label='Позиция клипа']", "1150000");
        await Wait("v.paused && !v.seeking && Math.abs(v.currentTime-1.15)<.002", "Timeline seek stopped audition");
        await Click("Прослушать фразу");
        await Wait("!v.paused", "Playback before trimming");
        await Field(".edit-times label:last-child input", "8.20");
        await Wait("v.paused && document.querySelectorAll('.caption-cue').length===2", "Trimming stopped audition and revealed the next cue");
        await Click("Прослушать фразу");
        await Wait("!v.paused && v.currentTime>.43 && v.currentTime<.9", "First cue before switching");
        await Eval("events.length=0; document.querySelector('[aria-label=\"Прослушать фразу 2\"]').click()");
        await Wait("events.some(e=>e.type==='seeked' && Math.abs(e.t-7.50)<.04) && !v.paused && v.currentTime>7.61 && v.currentTime<8.20",
            "Switching phrases seeks to the second cue");
        await Wait("v.paused && !v.seeking && Math.abs(v.currentTime-8.20)<.002", "Second phrase stops at clip Out");
        Console.WriteLine("PASS switched directly to second cue at 7.50..8.20 s: " + await Eval("events.filter(e=>e.type!=='timeupdate')"));
        await Click("Прослушать фразу");
        await Wait("!v.paused", "Playback before close");
        await Click("К проекту");
        await Wait("!document.querySelector('dialog') && v.paused", "Closing the editor stopped its media");
        await Click(project.Edits.Single(e => e.Id != clip.Id).Title);
        await Wait("document.querySelector('.edit-frame video')?.readyState >= 2 && document.querySelector('.edit-frame video').paused && v.paused",
            "Opening the other clip does not carry over a playing range");
        if (!original.SequenceEqual(HashFile(source)) || transcript != JsonSerializer.Serialize(library.Open(project.Id).Transcript))
            throw new InvalidDataException("Audition changed the original media or transcript.");
        Console.WriteLine("PASS replay after timing/trim changes, manual stop, full clip, seek and close; original source and ASR preserved.");
    }
}
