namespace Nexus.Data.Support;

public interface INexusDbFactory
{
    NexusDbContext Create();
}

/// <summary>Creates a context from the current configuration, so a changed connection applies without restart.</summary>
public sealed class SettingsDbFactory(SettingsProvider settings) : INexusDbFactory
{
    public NexusDbContext Create() => NexusDatabase.Create(settings.Current.Database);
}
