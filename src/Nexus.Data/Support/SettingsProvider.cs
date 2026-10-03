using Nexus.Core;
using Nexus.Core.Configuration;

namespace Nexus.Data.Support;

/// <summary>Reloads config/nexus.json when it changes, so 'nexusctl configure' applies without restart.</summary>
public sealed class SettingsProvider(NexusPaths paths)
{
    private readonly SettingsStore _store = new(paths);
    private readonly Lock _lock = new();
    private DateTime _loadedWriteTime;
    private NexusSettings _settings = new();

    public bool Exists => _store.Exists;

    public NexusSettings Current
    {
        get
        {
            lock (_lock)
            {
                var writeTime = _store.Exists ? File.GetLastWriteTimeUtc(paths.ConfigFile) : DateTime.MinValue;
                if (writeTime != _loadedWriteTime)
                {
                    _settings = _store.Load();
                    _loadedWriteTime = writeTime;
                }

                return _settings;
            }
        }
    }
}
