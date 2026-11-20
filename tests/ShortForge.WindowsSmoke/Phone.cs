using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    public async Task RunPhone(string phase, string path)
    {
        // The calling script stores selected technical fields only. Keep personal media out of the report.
        using var probe = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "reference.json")));
        var source = probe.RootElement;
        var width = source.GetProperty("width").GetInt32();
        var height = source.GetProperty("height").GetInt32();
        var rotation = source.GetProperty("rotation").GetInt32();
        if (phase == "phone")
        {
            await Contains("Добавьте первое видео");
            await DropFile(path);
        }
        else
        {
            await Wait("document.querySelectorAll('.project-row').length === 1", "One saved project");
            await Eval("document.querySelector('.project-row').click()");
        }
        await Contains(Path.GetFileNameWithoutExtension(path));
        await Contains($"{width} × {height}");
        await Contains(rotation + "°");
        await Contains(source.GetProperty("codec").GetString()!);
        await CheckFrameIntervals(phase == "phone", source.GetProperty("sampleFrames").GetInt32(),
            source.GetProperty("sampleMinMs").GetDouble(), source.GetProperty("sampleMaxMs").GetDouble());
        await Wait("document.querySelector('video')?.videoWidth > 0 || document.querySelector('.preview-fallback')", "Player result");
        Console.WriteLine($"WebView2={Web.Environment.BrowserVersionString}; phase={phase}; stored geometry={width}x{height}; rotation={rotation}");
        if (await Eval("!!document.querySelector('.preview-fallback')") == "true")
        {
            if (await Eval("!!document.querySelector('#hdr-proxy-note')") == "true")
            {
                await Contains("Предпросмотр недоступен");
                await Wait("!!document.querySelector('button[aria-describedby=hdr-proxy-note]:disabled')", "HDR proxy restriction");
                Console.WriteLine("Original playback unavailable in WebView2. HDR proxy restriction displayed. Seek and A/V check unavailable.");
                return;
            }
            bool hdrCopy = await Eval("Array.from(document.querySelectorAll('button')).some(b=>b.textContent.includes('Создать SDR-прокси'))") == "true";
            await Click(hdrCopy ? "Создать SDR-прокси" : "Создать прокси");
            await Contains(hdrCopy ? "Предпросмотр · SDR-прокси" : "Предпросмотр · Прокси");
            await Wait("document.querySelector('video')?.videoWidth > 0", "Proxy playback");
        }
        Console.WriteLine("Decoded player dimensions " + await Eval("({width:document.querySelector('video').videoWidth,height:document.querySelector('video').videoHeight})"));
        if (Math.Abs(rotation) % 180 == 90) (width, height) = (height, width);
        // This check is for square-pixel phone recordings.
        await Wait($"Math.abs(document.querySelector('video').videoWidth / document.querySelector('video').videoHeight - {width}/{height}) < .005", "Display aspect after rotation");
        await Eval("""
            { const v=document.querySelector('video');
              const ctx=new AudioContext({sampleRate:16000});
              const analyser=ctx.createAnalyser(); analyser.fftSize=2048;
              const gain=ctx.createGain(); gain.gain.value=0;
              ctx.createMediaElementSource(v).connect(analyser).connect(gain).connect(ctx.destination);
              window.phoneAnalyser=analyser; void ctx.resume(); }
            """);
        foreach (var fraction in new[] { ".15", ".5", ".8" })
        {
            await Eval($$"""
                { const v=document.querySelector('video'); v.pause(); window.phoneFrame=null;
                  window.phoneTarget=v.duration*{{fraction}};
                  v.requestVideoFrameCallback((_,m)=>{window.phoneFrame=m.mediaTime});
                  v.currentTime=window.phoneTarget; }
                """);
            await Wait("!document.querySelector('video').seeking && window.phoneFrame != null && Math.abs(window.phoneFrame-window.phoneTarget)<0.09", "Decoded frame at seek target");
            Console.WriteLine("Seek " + await Eval("({targetSeconds:window.phoneTarget,frameSeconds:window.phoneFrame})"));
            await CheckOrientation(path);
            await Eval("""
                { const v=document.querySelector('video');
                  window.phoneSample=null;
                  v.requestVideoFrameCallback(function sample(_,m) {
                    if(m.mediaTime < window.phoneTarget + .20) {v.requestVideoFrameCallback(sample); return;}
                    const data=new Float32Array(window.phoneAnalyser.fftSize);
                    window.phoneAnalyser.getFloatTimeDomainData(data);
                    window.phoneSample={time:m.mediaTime,data:Array.from(data)};
                  });
                  void v.play(); }
                """);
            await Wait("!!window.phoneSample", "Frame and audio samples after seek");
            using var sample = JsonDocument.Parse(await Eval("window.phoneSample"));
            var capture = sample.RootElement;
            await MeasureAudioAsync(path, capture.GetProperty("time").GetDouble(),
                capture.GetProperty("data").Deserialize<float[]>()!);
        }
        await Eval("document.querySelector('video').pause()");
    }

    private static async Task<byte[]> DecodeReference(string path, double seconds, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("SHORTFORGE_FFMPEG_BIN")!, "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-v","error","-nostdin","-ss",seconds.ToString("F6",CultureInfo.InvariantCulture),"-i",path }
            .Concat(args).Append("pipe:1")) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException("Reference decode failed.");
        return bytes.ToArray();
    }

    private async Task CheckOrientation(string path)
    {
        var frame = JsonSerializer.Deserialize<double>(await Eval("window.phoneFrame"));
        var observed = JsonSerializer.Deserialize<double[]>(await Eval("""
            (() => { const c=document.createElement('canvas'); c.width=12; c.height=20;
              const ctx=c.getContext('2d'); ctx.imageSmoothingQuality='high';
              ctx.drawImage(document.querySelector('video'),0,0,12,20);
              const pixels=ctx.getImageData(0,0,12,20).data;
              return Array.from({length:240},(_,i)=>.2126*pixels[i*4]+.7152*pixels[i*4+1]+.0722*pixels[i*4+2]);
            })()
            """))!;
        var reference = await DecodeReference(path, Math.Max(0,frame-.0001), "-map","0:v:0","-frames:v","1",
            "-vf","scale=12:20:flags=area,format=gray","-f","rawvideo");
        if (reference.Length != observed.Length) throw new InvalidDataException("Incomplete orientation reference.");
        var meanX = reference.Average(x => (double)x);
        var meanY = observed.Average();
        double normX=0, normY=0, upright=0, inverted=0;
        for (int i=0; i<observed.Length; i++)
        {
            double x=reference[i]-meanX, y=observed[i]-meanY;
            normX+=x*x; normY+=y*y;
            upright+=x*y; inverted+=(reference[^(i+1)]-meanX)*y;
        }
        var norm = Math.Sqrt(normX*normY);
        if (norm < 1) { Console.WriteLine("Uniform frame: orientation inconclusive."); return; }
        upright/=norm; inverted/=norm;
        Console.WriteLine($"Luminance correlation with autorotated reference = {upright:F3}; upside down = {inverted:F3}");
        if (inverted > .70 && inverted > upright+.10) throw new InvalidDataException("Player frame is upside down.");
        if (upright < .70 || upright < inverted+.10) Console.WriteLine("Orientation inconclusive for this frame.");
    }

    // Reference samples and player samples stay in process memory.
    private static async Task MeasureAudioAsync(string path, double frameTime, float[] wave)
    {
        const int rate = 16000;
        var start = Math.Max(0, frameTime - .5);
        var bytes = await DecodeReference(path, start, "-map","0:a:0","-t","0.85","-ar","16000","-ac","1","-f","f32le");
        var original = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, original, 0, original.Length * sizeof(float));
        double energy = wave.Sum(v => (double)v*v);
        var scores = new double[Math.Max(0, original.Length - wave.Length + 1)];
        for (int i = 0; i < scores.Length; i++)
        {
            double dot = 0, power = 0;
            for (int j = 0; j < wave.Length; j++)
            {
                double x = original[i+j]; dot += x*wave[j]; power += x*x;
            }
            if (power*energy > 1e-12) scores[i] = dot / Math.Sqrt(power*energy);
        }
        var best = scores.DefaultIfEmpty().Max();
        var offset = Array.IndexOf(scores,best);
        var alternative = scores.Where((_,i) => Math.Abs(i-offset) > rate*.03).DefaultIfEmpty().Max();
        if (best < .7 || best-alternative < .06)
        {
            Console.WriteLine($"Audio alignment inconclusive at {frameTime:F3}s (correlation {best:F3}, other peak {alternative:F3}).");
            return;
        }
        double deltaMs = 1000*(start + (offset + wave.Length)/(double)rate - frameTime);
        Console.WriteLine($"At video PTS {frameTime:F3}s: decoded audio window end minus frame PTS = {deltaMs:F1} ms (correlation {best:F3}).");
    }
}
