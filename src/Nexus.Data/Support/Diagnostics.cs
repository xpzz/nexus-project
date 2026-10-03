using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexus.Core;
using Nexus.Core.Configuration;

namespace Nexus.Data.Support;

/// <summary>"Gerar pacote de diagnóstico" (SPEC §2.12): logs, versions, settings without secrets, last checks.</summary>
public static class Diagnostics
{
    public static async Task CreateAsync(NexusPaths paths, SettingsStore store, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var file = File.Create(target);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        var info = new StringBuilder()
            .AppendLine($"Gerado em: {DateTimeOffset.Now:O}")
            .AppendLine($"Versão nexusctl: {Assembly.GetExecutingAssembly().GetName().Version}")
            .AppendLine($"Runtime: {Environment.Version}  SO: {Environment.OSVersion}  64 bits: {Environment.Is64BitOperatingSystem}")
            .AppendLine($"Máquina: {Environment.MachineName}  Pasta de dados: {paths.DataDirectory}")
            .ToString();
        await AddText(zip, "sistema.txt", info, ct);

        if (store.Exists)
        {
            var settings = store.Load();
            await AddText(zip, "configuracao.json", SettingsStore.Export(settings), ct);
            try
            {
                await using var db = NexusDatabase.Create(settings.Database);
                var health = await db.HealthResults.AsNoTracking().OrderByDescending(h => h.Id).Take(50).ToListAsync(ct);
                var jobs = await db.Jobs.AsNoTracking().ToListAsync(ct);
                await AddText(zip, "verificacoes.json", System.Text.Json.JsonSerializer.Serialize(new { jobs, health }, SettingsStore.JsonOptions), ct);
            }
            catch (Exception ex)
            {
                await AddText(zip, "verificacoes.txt", $"Banco indisponível ao gerar o pacote: {ex.Message}", ct);
            }
        }

        if (Directory.Exists(paths.LogsDirectory))
        {
            foreach (var log in new DirectoryInfo(paths.LogsDirectory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(10))
            {
                await using var source = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var entry = zip.CreateEntry($"logs/{log.Name}").Open();
                await source.CopyToAsync(entry, ct);
            }
        }
    }

    private static async Task AddText(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        await using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        await writer.WriteAsync(content.AsMemory(), ct);
    }
}
