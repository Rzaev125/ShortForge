using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

internal static class MediaProcess
{
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, int outputLimit, CancellationToken cancellationToken, Action<long>? encodedTime = null,
        Action<long>? observePeakBytes = null, string? workingDirectory = null, bool checkOutputErrors = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executable))
            throw MissingTool();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (workingDirectory != null) start.WorkingDirectory = workingDirectory;
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException) { throw MissingTool(); }

        using var childJob = WindowsChildJob.Attach(process);
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        // Closing both pipes after a kill also unblocks a read on older Windows hosts.
        using var registration = deadline.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) when (process.HasExited) { }
        });
        var output = encodedTime == null ? ReadAsync(process.StandardOutput, outputLimit, deadline.Token)
            : ReadProgressAsync(process.StandardOutput, encodedTime, deadline.Token);
        // Drain diagnostics in parallel. They can contain private source paths and metadata.
        var errors = ReadErrorsAsync(process.StandardError, checkOutputErrors, deadline.Token);
        try
        {
            var exit = process.WaitForExitAsync(deadline.Token);
            if (observePeakBytes != null)
                while (!exit.IsCompleted)
                {
                    try { process.Refresh(); observePeakBytes(process.PeakWorkingSet64); }
                    catch (InvalidOperationException) when (process.HasExited) { break; }
                    await Task.WhenAny(exit, Task.Delay(100));
                }
            await Task.WhenAll(output, errors, exit);
        }
        catch (Exception) when (deadline.IsCancellationRequested) { }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested)
            throw new UserError("tool_timeout", "Инструмент не ответил вовремя. Проверьте файл и повторите попытку.");
        if (process.ExitCode != 0)
            throw await errors ?? new UserError("media_unreadable", "Не удалось прочитать файл. Проверьте формат и целостность видео.");
        var (value, truncated) = await output;
        if (outputLimit > 0 && truncated) throw new UserError("metadata_limit", "Объём метаданных файла превышает допустимый размер.");
        return value;
    }

    private static UserError MissingTool() =>
        new("tool_unavailable", "Инструмент не запускается. Проверьте путь к программе в настройках.");

    private static async Task<UserError?> ReadErrorsAsync(StreamReader reader, bool checkOutputErrors, CancellationToken token)
    {
        var buffer = new char[4096];
        string tail = "";
        UserError? error = null;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (!checkOutputErrors || error != null) continue;
            var chunk = tail + new string(buffer, 0, count);
            if (chunk.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
                error = new("export_disk_full", "На диске назначения недостаточно свободного места.");
            else if (chunk.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                error = new("export_access", "Нет доступа к файлу при записи видео. Проверьте права и выберите доступную папку.");
            tail = chunk.Length > 32 ? chunk[^32..] : chunk;
        }
        return error;
    }

    // With -progress pipe:1 stdout contains FFmpeg's machine-generated key/value
    // records. Consume updates as they arrive instead of retaining a job-length log.
    private static async Task<(string Text, bool Truncated)> ReadProgressAsync(StreamReader reader,
        Action<long> encodedTime, CancellationToken token)
    {
        const string key = "out_time_us=";
        while (await reader.ReadLineAsync(token) is { } line)
            if (line.StartsWith(key, StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(key.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds))
                encodedTime(microseconds);
        return ("", false);
    }

    private static async Task<(string Text, bool Truncated)> ReadAsync(StreamReader reader, int limit,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            int take = Math.Min(count, limit - text.Length);
            text.Append(buffer, 0, take);
            truncated |= take != count;
        }
        return (text.ToString(), truncated);
    }
}
