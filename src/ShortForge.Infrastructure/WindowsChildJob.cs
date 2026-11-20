using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ShortForge.Core;

namespace ShortForge.Infrastructure;

internal static class WindowsChildJob
{
    // One non-inheritable handle per tool. Windows also closes it if the host crashes.
    internal static SafeFileHandle? Attach(Process child)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const uint killOnJobClose = 0x2000;
        const int extendedLimitInformation = 9;
        var job = CreateJobObjectW(0, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = killOnJobClose } };
        if (!job.IsInvalid && SetInformationJobObject(job, extendedLimitInformation, ref limits,
            (uint)Marshal.SizeOf<ExtendedLimits>()) && AssignProcessToJobObject(job, child.SafeHandle))
            return job;

        job.Dispose();
        // A short-lived probe may already have returned before assignment.
        if (child.HasExited) return null;
        try { child.Kill(entireProcessTree: true); child.WaitForExit(); }
        catch (InvalidOperationException) when (child.HasExited) { }
        catch (Win32Exception) when (child.HasExited) { }
        throw new UserError("process_job", "Windows не удалось связать процесс обработки с приложением.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
