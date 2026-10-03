using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Worker.Platform;

/// <summary>
/// Puts the Worker in a Windows Job Object with a hard CPU cap and a memory limit, and lowers
/// its priority, so it never competes with the SCCM site (SPEC §5.3).
/// </summary>
public static partial class ResourceLimiter
{
    public static string Apply(int maxCpuPercent, int maxMemoryMegabytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Limites de CPU e memória por Job Object só se aplicam no Windows.";
        }

        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        return ApplyJobObject(Math.Clamp(maxCpuPercent, 1, 100), Math.Max(256, maxMemoryMegabytes));
    }

    [SupportedOSPlatform("windows")]
    private static string ApplyJobObject(int cpuPercent, int memoryMegabytes)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return $"Não foi possível criar o Job Object (erro {Marshal.GetLastPInvokeError()}).";
        }

        var cpu = new JobObjectCpuRateControlInformation
        {
            ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
            CpuRate = (uint)(cpuPercent * 100),
        };
        if (!SetInformationJobObject(job, JobObjectCpuRateControlInformationClass, ref cpu, (uint)Marshal.SizeOf<JobObjectCpuRateControlInformation>()))
        {
            return $"Não foi possível limitar a CPU (erro {Marshal.GetLastPInvokeError()}).";
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitProcessMemory },
            ProcessMemoryLimit = (UIntPtr)((ulong)memoryMegabytes * 1024 * 1024),
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            return $"Não foi possível limitar a memória (erro {Marshal.GetLastPInvokeError()}).";
        }

        if (!AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
        {
            return $"Não foi possível associar o processo ao Job Object (erro {Marshal.GetLastPInvokeError()}).";
        }

        // The handle stays open for the lifetime of the process on purpose.
        return $"Limites aplicados: CPU {cpuPercent}% e memória {memoryMegabytes} MB, prioridade abaixo do normal.";
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectCpuRateControlInformationClass = 15;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;
    private const uint JobObjectLimitProcessMemory = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectCpuRateControlInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
