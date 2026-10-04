using System.CommandLine;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nexus.Cli;
using Nexus.Collectors.Sccm;
using Nexus.Core;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Core.Health;
using Nexus.Core.Security;
using Nexus.Data;
using Nexus.Data.Support;
using Nexus.Data.Entities;
using Nexus.Reconciliation;

// Exit codes: 0 ok, 1 checks with errors, 2 Worker did not respond, 3 invalid usage/environment.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new System.Globalization.CultureInfo("pt-BR");
var paths = NexusPaths.Resolve();
var store = new SettingsStore(paths);
var actor = Actor();

var root = new RootCommand("nexusctl — operação do Azul Nexus");

// configure --from <install.resolved.json>
var fromOption = new Option<FileInfo>("--from") { Description = "Arquivo de instalação com os valores resolvidos.", Required = true };
var configure = new Command("configure", "Grava a configuração a partir do arquivo de instalação (preserva o que já existe).") { fromOption };
configure.SetAction(async (parse, ct) =>
{
    var answers = JsonSerializer.Deserialize<InstallAnswers>(await File.ReadAllTextAsync(parse.GetValue(fromOption)!.FullName, ct), SettingsStore.JsonOptions)!;
    var settings = answers.ApplyTo(store.Load());
    // The PostgreSQL password comes only from NEXUS_DB_PASSWORD (never from the install file)
    // and is stored protected with DPAPI. Outside Windows (development) it stays in the variable.
    if (settings.Database.Provider == DatabaseProvider.PostgreSql
        && Environment.GetEnvironmentVariable("NEXUS_DB_PASSWORD") is { Length: > 0 } password
        && OperatingSystem.IsWindows())
    {
        settings.Database.ProtectedPassword = new DpapiSecretProtector().Protect(password);
    }

    store.Save(settings);
    Console.WriteLine($"Configuração gravada em {paths.ConfigFile}.");
    return 0;
});
root.Subcommands.Add(configure);

// migrate
var migrate = new Command("migrate", "Aplica as migrações do banco do Nexus com a identidade de quem executa.");
migrate.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    await using var db = NexusDatabase.Create(settings.Database);
    try
    {
        var applied = await NexusDatabase.MigrateAsync(db, ct);
        Console.WriteLine(applied.Count == 0 ? "Banco já está na versão atual." : $"Migrações aplicadas: {string.Join(", ", applied)}.");
        return 0;
    }
    catch (Exception ex) when (ex is SqlException or Npgsql.NpgsqlException or InvalidOperationException)
    {
        Console.Error.WriteLine(ErrorCatalog.DatabaseUnreachable.WithDetail(ex.Message));
        Console.Error.WriteLine("Sem permissão para criar ou alterar o banco? Gere o script para o DBA com 'nexusctl db-script --output nexus-db.sql'.");
        return 1;
    }
});
root.Subcommands.Add(migrate);

// db-script --output
var outputOption = new Option<FileInfo?>("--output", "-o") { Description = "Arquivo de saída. Sem ele, escreve na tela." };
var dbScript = new Command("db-script", "Gera o script idempotente das migrações para o DBA.") { outputOption };
dbScript.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    await using var db = NexusDatabase.Create(settings.Database);
    var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    await Write(parse.GetValue(outputOption), script, ct);
    return 0;
});
root.Subcommands.Add(dbScript);

// db-grant-script --account ... --output
var accountsOption = new Option<string[]>("--account") { Description = "Conta que precisa de acesso (repita para várias).", Required = true, AllowMultipleArgumentsPerToken = true };
var dbGrant = new Command("db-grant-script", "Gera o T-SQL que dá às contas dos serviços acesso de leitura e escrita ao banco do Nexus.") { accountsOption, outputOption };
dbGrant.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    if (settings.Database.Provider != DatabaseProvider.SqlServer)
    {
        Console.Error.WriteLine("No PostgreSQL o acesso é por usuário e senha definidos pelo DBA; não há script de logins Windows.");
        return 3;
    }

    var script = DatabaseScripts.SqlServerGrants(NexusDatabase.DatabaseName(settings.Database), parse.GetValue(accountsOption)!);
    await Write(parse.GetValue(outputOption), script, ct);
    return 0;
});
root.Subcommands.Add(dbGrant);

// db-grant --account ... (applies with the caller identity)
var dbGrantApply = new Command("db-grant", "Aplica o script de acesso ao banco do Nexus com a identidade de quem executa.") { accountsOption };
dbGrantApply.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var script = DatabaseScripts.SqlServerGrants(NexusDatabase.DatabaseName(settings.Database), parse.GetValue(accountsOption)!);
    var master = new SqlConnectionStringBuilder(settings.Database.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
    return await ApplyBatches(master, script, ct);
});
root.Subcommands.Add(dbGrantApply);

// sccm-grant-script / sccm-grant
var sccmAccountOption = new Option<string>("--account") { Description = "Conta do serviço Worker vista pelo SQL do site (ex.: NT SERVICE\\AzulNexus.Worker ou DOMINIO\\gmsa$).", Required = true };
var revokeOption = new Option<bool>("--revoke") { Description = "Gera o script de reversão." };
var sccmGrantScript = new Command("sccm-grant-script", "Gera o T-SQL de leitura das views do SCCM para o DBA (papel dedicado, reversível).") { sccmAccountOption, revokeOption, outputOption };
sccmGrantScript.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var account = parse.GetValue(sccmAccountOption)!;
    var script = parse.GetValue(revokeOption)
        ? SccmGrantScript.Revoke(settings.Sccm.Database, account)
        : SccmGrantScript.Grant(settings.Sccm.Database, account);
    await Write(parse.GetValue(outputOption), script, ct);
    return 0;
});
root.Subcommands.Add(sccmGrantScript);

var sccmGrant = new Command("sccm-grant", "Aplica a concessão de leitura das views do SCCM com a identidade de quem executa.") { sccmAccountOption };
sccmGrant.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var script = SccmGrantScript.Grant(settings.Sccm.Database, parse.GetValue(sccmAccountOption)!);
    var connection = new SqlConnectionStringBuilder(SccmConnectionFactory.BuildConnectionString(settings.Sccm))
    {
        InitialCatalog = "master",
        ApplicationIntent = ApplicationIntent.ReadWrite,
    }.ConnectionString;
    return await ApplyBatches(connection, script, ct);
});
root.Subcommands.Add(sccmGrant);

// test
var timeoutOption = new Option<int>("--timeout") { Description = "Segundos de espera pelo Worker.", DefaultValueFactory = _ => 120 };
var jsonOption = new Option<bool>("--json") { Description = "Saída em JSON." };
var test = new Command("test", "Pede ao Worker que rode todas as verificações com a conta do serviço e mostra o resultado.") { timeoutOption, jsonOption };
test.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var command = await Enqueue(settings, CommandTypes.RunHealthChecks, null, ct);
    var done = await CommandQueue.WaitAsync(() => NexusDatabase.Create(settings.Database), command.Id, TimeSpan.FromSeconds(parse.GetValue(timeoutOption)), ct);
    if (done?.Result is null)
    {
        Console.Error.WriteLine(ErrorCatalog.WorkerNotResponding);
        return 2;
    }

    var results = JsonSerializer.Deserialize<List<HealthCheckResult>>(done.Result, SettingsStore.JsonOptions)!;
    if (parse.GetValue(jsonOption))
    {
        Console.WriteLine(done.Result);
    }
    else
    {
        foreach (var r in results)
        {
            Console.WriteLine($"[{Label(r.Status)}] {r.Name}: {r.Message}");
            if (r.Error is { } e && r.Status == HealthStatus.Error)
            {
                Console.WriteLine($"         Impacto: {e.Impact}");
                Console.WriteLine($"         Como resolver: {e.HowToFix}");
            }
        }
    }

    return results.Any(r => r.Status == HealthStatus.Error) ? 1 : 0;
});
root.Subcommands.Add(test);

// collect [all|sccm|ad]
var sourceArgument = new Argument<string>("fonte") { Description = "all, sccm, ad, intune, entra ou inventory.", DefaultValueFactory = _ => "all" };
var waitOption = new Option<bool>("--wait") { Description = "Aguarda o fim da coleta." };
var collect = new Command("collect", "Pede ao Worker uma coleta agora (respeita pausas e limites).") { sourceArgument, waitOption, timeoutOption };
collect.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var source = parse.GetValue(sourceArgument) switch { "sccm" => "sccm", "ad" => "ad", "intune" => "intune", "entra" => "entra", "inventory" => "inventory", _ => "all" };
    var command = await Enqueue(settings, CommandTypes.CollectNow, source, ct);
    if (!parse.GetValue(waitOption))
    {
        Console.WriteLine($"Coleta solicitada (comando {command.Id}).");
        return 0;
    }

    var done = await CommandQueue.WaitAsync(() => NexusDatabase.Create(settings.Database), command.Id, TimeSpan.FromSeconds(parse.GetValue(timeoutOption)), ct);
    if (done is null)
    {
        Console.Error.WriteLine(ErrorCatalog.WorkerNotResponding);
        return 2;
    }

    Console.WriteLine(done.Result);
    return done.Status == CommandStatus.Succeeded ? 0 : 1;
});
root.Subcommands.Add(collect);

// pause / resume
foreach (var (name, paused) in new[] { ("pause", true), ("resume", false) })
{
    var cmd = new Command(name, paused ? "Pausa os coletores." : "Retoma os coletores.");
    cmd.SetAction(async (parse, ct) =>
    {
        if (!RequireConfig(out var settings)) return 3;
        settings.Collection.Paused = paused;
        store.Save(settings);
        await Enqueue(settings, paused ? CommandTypes.PauseCollectors : CommandTypes.ResumeCollectors, null, ct);
        Console.WriteLine(paused ? "Coletores pausados." : "Coletores retomados.");
        return 0;
    });
    root.Subcommands.Add(cmd);
}

// status
var status = new Command("status", "Mostra coletas e o último resultado das verificações.");
status.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    await using var db = NexusDatabase.Create(settings.Database);
    Console.WriteLine($"Dados: {paths.DataDirectory}");
    Console.WriteLine($"Coletores: {(settings.Collection.Paused ? "pausados" : "ativos")}");
    foreach (var job in await db.Jobs.AsNoTracking().OrderBy(j => j.Name).ToListAsync(ct))
    {
        Console.WriteLine($"  {job.Name}: {job.LastStatus} · registros {job.LastRecordCount?.ToString() ?? "-"} · último sucesso {job.LastSuccessAt?.ToLocalTime():g} · próxima {job.NextRunAt?.ToLocalTime():g}");
    }

    var lastRun = await db.HealthResults.OrderByDescending(h => h.Id).Select(h => (Guid?)h.RunId).FirstOrDefaultAsync(ct);
    if (lastRun is not null)
    {
        foreach (var h in await db.HealthResults.Where(h => h.RunId == lastRun).OrderBy(h => h.Id).ToListAsync(ct))
        {
            Console.WriteLine($"  [{h.Status}] {h.Name}: {h.Message} (como {h.ExecutedAs}, {h.CheckedAt.ToLocalTime():g})");
        }
    }

    return 0;
});
root.Subcommands.Add(status);

// kpis
var kpis = new Command("kpis", "Mostra os indicadores de cobertura com numerador, denominador e estado (disponível, não habilitado, sem dados).");
kpis.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    await using var db = NexusDatabase.Create(settings.Database);
    var report = await InventoryReports.BuildAsync(db, ct);
    foreach (var k in report.Kpis)
    {
        var value = k.State == KpiState.Available ? $"{k.Percent?.ToString("0.0") ?? "-"}% ({k.Numerator}/{k.Denominator})" : k.State == KpiState.NotEnabled ? "não habilitado" : "sem dados";
        Console.WriteLine($"{k.Label}: {value}{(k.Note is null ? "" : " — " + k.Note)}");
    }

    var c = report.Counts;
    Console.WriteLine($"Ativos únicos: {c.Total} (ativos {c.Active}, desatualizados {c.Stale}) · só SCCM {c.OnlySccm} · só Intune {c.OnlyIntune} · ambos {c.Both} · nenhum {c.Neither}");
    Console.WriteLine($"SCCM sem cliente: {c.SccmWithoutClient} · para revisão: {c.Review} · pessoais: {c.PersonalDevices} · celulares corporativos: {c.CorporateMobile}");
    return 0;
});
root.Subcommands.Add(kpis);

// setup-code / recover-access
var setupCode = new Command("setup-code", "Gera um novo código de configuração inicial (uso único, 24 h). Só administradores locais.");
setupCode.SetAction(async (parse, ct) =>
{
    if (!RequireAdmin() || !RequireConfig(out var settings)) return 3;
    await using var db = NexusDatabase.Create(settings.Database);
    var state = await SetupModeService.GetAsync(db, ct);
    if (!state.SetupModeActive)
    {
        Console.Error.WriteLine("O modo de configuração já foi encerrado (SSO ativo). Para reabri-lo, use 'nexusctl recover-access'.");
        return 3;
    }

    var code = await SetupModeService.IssueCodeAsync(db, actor, TimeProvider.System, ct);
    Console.WriteLine($"Código de configuração: {code} (válido por 24 horas, uso único)");
    return 0;
});
root.Subcommands.Add(setupCode);

var recover = new Command("recover-access", "Reabre o modo de configuração e registra o evento na auditoria. Só administradores locais.");
recover.SetAction(async (parse, ct) =>
{
    if (!RequireAdmin() || !RequireConfig(out var settings)) return 3;
    await using var db = NexusDatabase.Create(settings.Database);
    var code = await SetupModeService.RecoverAccessAsync(db, actor, TimeProvider.System, ct);
    Console.WriteLine($"Modo de configuração reaberto. Código: {code} (válido por 24 horas, uso único)");
    return 0;
});
root.Subcommands.Add(recover);

// config export / import
var config = new Command("config", "Exporta ou importa a configuração sem segredos.");
var exportCmd = new Command("export", "Exporta a configuração sem segredos.") { outputOption };
exportCmd.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    await Write(parse.GetValue(outputOption), SettingsStore.Export(settings), ct);
    return 0;
});
var inputOption = new Option<FileInfo>("--input", "-i") { Description = "Arquivo exportado de outro ambiente.", Required = true };
var importCmd = new Command("import", "Importa a configuração mantendo os segredos locais.") { inputOption };
importCmd.SetAction(async (parse, ct) =>
{
    var imported = SettingsStore.Import(await File.ReadAllTextAsync(parse.GetValue(inputOption)!.FullName, ct), store.Load());
    Backup.Config(paths);
    store.Save(imported);
    Console.WriteLine("Configuração importada. A anterior foi copiada para a pasta de backups.");
    return 0;
});
config.Subcommands.Add(exportCmd);
config.Subcommands.Add(importCmd);
root.Subcommands.Add(config);

// backup
var backup = new Command("backup", "Copia a configuração e faz backup do banco do Nexus (COPY_ONLY no SQL Server, pg_dump no PostgreSQL).");
backup.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var folder = Backup.Config(paths);
    Console.WriteLine($"Configuração copiada para {folder}.");
    var (ok, message) = await Backup.DatabaseAsync(settings.Database, folder, ct);
    (ok ? Console.Out : Console.Error).WriteLine(message);
    return ok ? 0 : 1;
});
root.Subcommands.Add(backup);

// diagnostics
var diagnostics = new Command("diagnostics", "Gera o pacote de diagnóstico: logs, versões, configuração sem segredos e último resultado das verificações.") { outputOption };
diagnostics.SetAction(async (parse, ct) =>
{
    var target = parse.GetValue(outputOption)?.FullName ?? Path.Combine(paths.DataDirectory, $"diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    await Diagnostics.CreateAsync(paths, store, target, ct);
    Console.WriteLine($"Pacote de diagnóstico: {target}");
    return 0;
});
root.Subcommands.Add(diagnostics);

return await root.Parse(args).InvokeAsync();

bool RequireConfig(out NexusSettings settings)
{
    settings = store.Load();
    if (store.Exists)
    {
        return true;
    }

    Console.Error.WriteLine(ErrorCatalog.ConfigurationMissing.WithDetail($"Arquivo esperado: {paths.ConfigFile}."));
    return false;
}

bool RequireAdmin()
{
    if (!OperatingSystem.IsWindows() || new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    {
        return true;
    }

    Console.Error.WriteLine("Este comando exige um prompt de administrador local (Executar como administrador).");
    return false;
}

async Task<WorkerCommand> Enqueue(NexusSettings settings, string type, string? argument, CancellationToken ct)
{
    await using var db = NexusDatabase.Create(settings.Database);
    return await CommandQueue.EnqueueAsync(db, type, argument, actor, ct);
}

static async Task<int> ApplyBatches(string connectionString, string script, CancellationToken ct)
{
    try
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync(ct);
        }

        Console.WriteLine("Script aplicado.");
        return 0;
    }
    catch (SqlException ex)
    {
        Console.Error.WriteLine($"Não foi possível aplicar o script com a sua conta: {ex.Message}");
        Console.Error.WriteLine("Impacto: o acesso não foi concedido. Como resolver: gere o script com o comando '-script' correspondente e entregue ao DBA.");
        return 1;
    }
}

static async Task Write(FileInfo? output, string content, CancellationToken ct)
{
    if (output is null)
    {
        Console.WriteLine(content);
        return;
    }

    output.Directory?.Create();
    await File.WriteAllTextAsync(output.FullName, content, ct);
    Console.WriteLine($"Gravado em {output.FullName}.");
}

static string Label(HealthStatus status) => status switch
{
    HealthStatus.Ok => "OK      ",
    HealthStatus.Warning => "ATENÇÃO ",
    HealthStatus.Error => "ERRO    ",
    HealthStatus.NotConfigured => "PENDENTE",
    _ => "N/D     ",
};

static string Actor() => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : Environment.UserName;
