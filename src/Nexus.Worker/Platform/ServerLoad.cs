using System.Runtime.InteropServices;

namespace Nexus.Worker.Platform;

public interface IServerLoad
{
    /// <summary>Whole-server CPU usage in percent, or null when unknown.</summary>
    Task<double?> GetCpuPercentAsync(CancellationToken cancellationToken);
}

public sealed partial class WindowsServerLoad : IServerLoad
{
    public async Task<double?> GetCpuPercentAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle1, out var kernel1, out var user1))
        {
            return null;
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        if (!GetSystemTimes(out var idle2, out var kernel2, out var user2))
        {
            return null;
        }

        var idle = idle2 - idle1;
        var total = (kernel2 - kernel1) + (user2 - user1); // kernel time includes idle time
        return total <= 0 ? null : Math.Round(100.0 * (total - idle) / total, 1);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
