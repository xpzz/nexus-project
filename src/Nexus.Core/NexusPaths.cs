using Microsoft.Win32;

namespace Nexus.Core;

/// <summary>
/// Resolves the data directory shared by the Web (IIS) and Worker processes.
/// Order: HKLM\SOFTWARE\Azul\Nexus\DataDir, NEXUS_DATA_DIR, then a local development folder.
/// </summary>
public sealed class NexusPaths
{
    public const string RegistryKey = @"SOFTWARE\Azul\Nexus";
    public const string DataDirEnvironmentVariable = "NEXUS_DATA_DIR";

    public NexusPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string ConfigDirectory => Path.Combine(DataDirectory, "config");
    public string ConfigFile => Path.Combine(ConfigDirectory, "nexus.json");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");
    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    public string ScriptsDirectory => Path.Combine(DataDirectory, "scripts");

    public static NexusPaths Resolve()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
            if (key?.GetValue("DataDir") is string fromRegistry && fromRegistry.Length > 0)
            {
                return new NexusPaths(fromRegistry);
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new NexusPaths(fromEnvironment);
        }

        return new NexusPaths(Path.Combine(AppContext.BaseDirectory, ".nexus-data"));
    }
}
