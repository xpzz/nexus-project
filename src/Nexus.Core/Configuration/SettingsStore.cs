using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Core.Configuration;

public sealed class SettingsStore(NexusPaths paths)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool Exists => File.Exists(paths.ConfigFile);

    public NexusSettings Load()
    {
        if (!Exists)
        {
            return new NexusSettings();
        }

        using var stream = File.OpenRead(paths.ConfigFile);
        return JsonSerializer.Deserialize<NexusSettings>(stream, JsonOptions) ?? new NexusSettings();
    }

    /// <summary>Atomic write: a crash never leaves a half-written configuration.</summary>
    public void Save(NexusSettings settings)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        var temporary = paths.ConfigFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, paths.ConfigFile, overwrite: true);
    }

    /// <summary>Configuration without secrets, portable between environments.</summary>
    public static string Export(NexusSettings settings)
    {
        var copy = JsonSerializer.Deserialize<NexusSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
        copy.Database.ProtectedPassword = null;
        return JsonSerializer.Serialize(copy, JsonOptions);
    }

    /// <summary>Imports a portable configuration, keeping the local secrets of this server.</summary>
    public static NexusSettings Import(string json, NexusSettings current)
    {
        var imported = JsonSerializer.Deserialize<NexusSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("Arquivo de configuração vazio ou inválido.");
        imported.Database.ProtectedPassword = current.Database.ProtectedPassword;
        return imported;
    }
}
