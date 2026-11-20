using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

namespace ShortForge.WindowsSmoke;

internal sealed partial class Smoke
{
    private sealed record MemorySample(long ElapsedMs, long HostPrivateBytes, long WebPrivateBytes,
        long HostPeakCommitBytes, long LargestWebProcessPeakCommitBytes, long ManagedHeapBytes, int WebProcesses);

    private async Task SampleMemory(List<MemorySample> samples, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        using var host = Process.GetCurrentProcess();
        do
        {
            host.Refresh();
            long webPrivate = 0, webPeak = 0;
            int count = 0;
            bool renderer = false;
            foreach (var info in Web.Environment.GetProcessInfos())
            {
                try
                {
                    using var process = Process.GetProcessById(info.ProcessId);
                    webPrivate += process.PrivateMemorySize64;
                    webPeak = Math.Max(webPeak, process.PeakPagedMemorySize64);
                    renderer |= info.Kind == CoreWebView2ProcessKind.Renderer;
                    count++;
                }
                catch (ArgumentException) { /* A short-lived utility process exited. */ }
                catch (InvalidOperationException) { /* Process exited during measurement. */ }
            }
            if (!renderer) throw new InvalidOperationException("No WebView2 renderer in this test profile.");
            samples.Add(new(clock.ElapsedMilliseconds, host.PrivateMemorySize64, webPrivate,
                host.PeakPagedMemorySize64, webPeak, GC.GetTotalMemory(false), count));
            await Task.Delay(100);
        } while (!token.IsCancellationRequested);
    }

    public async Task RunLargeImport(string path)
    {
        var size = new FileInfo(path).Length;
        if (size <= 1L << 30) throw new InvalidOperationException("B06 requires a video larger than 1 GiB.");
        await Contains("Добавьте первое видео");
        int mediaResponses = 0;
        long largestResponse = 0;
        Web.WebResourceRequested += (_, e) =>
        {
            if (new Uri(e.Request.Uri).AbsolutePath.StartsWith("/media/", StringComparison.Ordinal) &&
                e.Request.Method == "GET" && e.Response?.Headers.Contains("Content-Length") == true)
            {
                mediaResponses++;
                var bytes = long.Parse(e.Response.Headers.GetHeader("Content-Length"));
                largestResponse = Math.Max(largestResponse, bytes);
                if (mediaResponses <= 3) Console.WriteLine($"Media range: {e.Response.Headers.GetHeader("Content-Range")}, bytes={bytes}");
            }
        };
        var samples = new List<MemorySample>();
        using var stop = new CancellationTokenSource();
        var monitor = SampleMemory(samples, stop.Token);
        try
        {
            // Observe real rendered progress and cancel through the ordinary app button.
            await Eval("""
                window.hashPercent = [];
                let cancelOnce = true;
                new MutationObserver(() => {
                  const bar = document.querySelector('[role=progressbar]');
                  if (!bar) return;
                  const n = Number(bar.getAttribute('aria-valuenow'));
                  if (window.hashPercent.at(-1) !== n) window.hashPercent.push(n);
                  if (cancelOnce && n > 0 && n < 100) {
                    cancelOnce = false;
                    document.querySelector('.job button:enabled')?.click();
                  }
                }).observe(document.querySelector('main'), {subtree:true,childList:true,attributes:true,characterData:true});
                """);
            await DropFile(path);
            await Contains("Операция отменена");
            Console.WriteLine("Cancelled during streamed fingerprint.");
            await Wait("document.querySelector('.dropzone button:enabled') && !document.querySelector('.project-row')", "Retryable empty library");
            await Click("Закрыть");
            await Eval("window.hashPercent.length = 0");
            await DropFile(path);
            await Wait("document.querySelector('video')?.readyState >= 1", "Large MP4 imported", 180);
            await VerifyPlayback();
            await Eval("const v=document.querySelector('video'); v.currentTime=v.duration-1");
            await Wait("!document.querySelector('video').seeking && document.querySelector('video').currentTime > document.querySelector('video').duration-2", "Seek near EOF");
            await Eval("void document.querySelector('video').play()");
            await Wait("document.querySelector('video').ended", "Playback through the final frame");
            await Eval("""
                void (async () => {
                  const url = document.querySelector('video').currentSrc;
                  const head = await fetch(url, {method:'HEAD'});
                  const whole = await fetch(url);
                  window.b06HeadSize = Number(head.headers.get('Content-Length'));
                  window.b06RequiresRange = whole.status === 400;
                })();
                """);
            await Wait($"window.b06HeadSize === {size} && window.b06RequiresRange", "Full length via HEAD, large GET requires a range");
            // Read only the last 16 bytes through the actual local media route.
            await Eval("""
                void fetch(document.querySelector('video').currentSrc,{headers:{Range:'bytes=-16'}})
                  .then(async r => {
                    if (r.status === 206 && r.headers.get('Content-Length') === '16')
                      window.b06Tail = Array.from(new Uint8Array(await r.arrayBuffer()));
                  });
                """);
            await Wait("window.b06Tail?.length === 16", "16-byte range near EOF");
            var expected = new byte[16];
            using (var source = File.OpenRead(path))
            {
                source.Seek(-expected.Length, SeekOrigin.End);
                source.ReadExactly(expected);
            }
            var actual = JsonSerializer.Deserialize<int[]>(await Eval("window.b06Tail"))!;
            if (!expected.Select(b => (int)b).SequenceEqual(actual)) throw new InvalidDataException("Incorrect range bytes.");
            if (mediaResponses < 2 || largestResponse > 2 * 1024 * 1024) throw new InvalidDataException("Expected bounded media responses.");
            var progress = JsonSerializer.Deserialize<int[]>(await Eval("window.hashPercent"))!;
            if (!progress.Any(n => n is > 0 and < 100) || !progress.SequenceEqual(progress.Order()))
                throw new InvalidDataException("Hash progress was missing or moved backwards.");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(output, "data", "library.sqlite3"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT size_bytes, sha256 FROM media_asset";
            using var reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != size) throw new InvalidDataException("Missing imported asset.");
            File.WriteAllText(Path.Combine(output, "import.sha256"), reader.GetString(1));
            if (reader.Read()) throw new InvalidDataException("Cancelled import left a duplicate.");
            Console.WriteLine($"Cancelled, retried, played, sought near EOF and verified final 16 bytes. Hash progress: {string.Join(",", progress)}");
        }
        finally
        {
            stop.Cancel();
            await monitor;
            File.WriteAllText(Path.Combine(output, "memory.json"), JsonSerializer.Serialize(new { fileBytes = size, samples }));
        }
        var baseline = samples[0];
        var peakHost = samples.Max(s => s.HostPeakCommitBytes);
        var peakWebProcess = samples.Max(s => s.LargestWebProcessPeakCommitBytes);
        var webGrowth = samples.Max(s => s.WebPrivateBytes) - baseline.WebPrivateBytes;
        Console.WriteLine($"Source bytes={size}; samples={samples.Count}; host peak commit={peakHost}; largest WebView2 process peak commit={peakWebProcess}; sampled WebView2 private growth={webGrowth}");
        if (samples.Count < 2 || peakHost <= 0 || peakWebProcess <= 0 || Math.Max(Math.Max(peakHost, peakWebProcess), webGrowth) >= size)
            throw new InvalidOperationException("B06 memory bounds were exceeded or could not be measured.");
    }
}
