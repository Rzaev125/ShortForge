using System.Diagnostics;
using ShortForge.Core;
using Xunit;

namespace ShortForge.Infrastructure.Tests;

public sealed class ProcessTests
{
    [Theory]
    [InlineData(" V....D libx264rgb H.264\n A....D aac AAC", false, true)]
    [InlineData(" V....D libx264 H.264\n A....D libfdk_aac AAC", true, false)]
    [InlineData(" V....D h264_nvenc alternative to libx264\n S..... text aac", false, false)]
    public void EncodersMatchExactNamesAndStreamTypes(string rows, bool libx264, bool aac)
    {
        Assert.Equal(new ProxyEncoders(libx264, aac), ProxyEncoders.Parse("Encoders:\r\n ------\r\n" + rows));
    }

    [Theory]
    [InlineData("ffmpeg version 9.1")]
    [InlineData("Encoders:\n ------\n")]
    [InlineData("Encoders:\n ------\n V....D libx264 H.264\n malformed")]
    public void UnreadableEncoderTablesGiveNoCapabilityResult(string output) =>
        Assert.Null(ProxyEncoders.Parse(output));

    [Fact]
    public async Task CancellationStopsTheRunningProcess()
    {
        var directory = Directory.CreateTempSubdirectory("ShortForge-cancel-");
        var marker = Path.Combine(directory.FullName, "started");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string exe = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
            // Pass the path using an environment-independent literal. Only a machine-generated
            // temporary name is interpolated; no application or media input is involved.
            string[] args = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-Command", "[IO.File]::WriteAllText('" +
                    marker.Replace("'", "''") + "', $PID.ToString()); Start-Sleep -Seconds 60"]
                : ["-c", "echo $$ > \"$1\"; exec sleep 60", "fixture", marker];
            var run = MediaProcess.RunAsync(exe, args, TimeSpan.FromMinutes(1), 512, cancellation.Token);
            int pid = 0;
            while (!cancellation.IsCancellationRequested)
            {
                if (run.IsCompleted) await run;
                if (File.Exists(marker) && int.TryParse(await File.ReadAllTextAsync(marker), out pid)) break;
                await Task.Delay(30, cancellation.Token);
            }
            Assert.True(pid > 0);
            using var child = Process.GetProcessById(pid);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(child.HasExited);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ToolWhichDoesNotFinishReportsTimeout()
    {
        string exe = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        string[] args = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"]
            : ["-c", "exec sleep 60"];
        var error = await Assert.ThrowsAsync<UserError>(() =>
            MediaProcess.RunAsync(exe, args, TimeSpan.FromMilliseconds(250), 512, default));
        Assert.Equal("tool_timeout", error.Code);
    }
}
