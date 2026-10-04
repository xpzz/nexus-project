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
using Nexus.Collectors.Netskope;
using Nexus.Collectors.Xdr;
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

// xdr-configure / xdr-grant-script / xdr-grant
var xdrServerOption = new Option<string>("--server") { Description = "Servidor SQL onde a rotina do Cortex XDR grava os endpoints." };
var xdrDatabaseOption = new Option<string>("--database") { Description = "Banco da tabela (padrão cortex_db)." };
var xdrTableOption = new Option<string>("--table") { Description = "Tabela, opcionalmente esquema.tabela (padrão API_Cortex_getAllEndpoints)." };
var xdrTrustOption = new Option<bool>("--trust-server-certificate") { Description = "Aceita o certificado do servidor SQL sem validar a cadeia." };
var xdrOffOption = new Option<bool>("--off") { Description = "Desliga a coleta do Cortex XDR." };
var xdrConfigure = new Command("xdr-configure", "Configura a leitura da tabela de endpoints do Cortex XDR (o Nexus não guarda a chave da API do XDR).") { xdrServerOption, xdrDatabaseOption, xdrTableOption, xdrTrustOption, xdrOffOption };
xdrConfigure.SetAction(parse =>
{
    if (!RequireConfig(out var settings)) return 3;
    if (parse.GetValue(xdrOffOption))
    {
        settings.Xdr.Mode = SourceMode.Disabled;
        store.Save(settings);
        Console.WriteLine("Coleta do Cortex XDR desligada.");
        return 0;
    }

    var server = parse.GetValue(xdrServerOption) ?? settings.Xdr.SqlServer;
    if (string.IsNullOrWhiteSpace(server))
    {
        Console.Error.WriteLine("Informe o servidor SQL com --server.");
        return 3;
    }

    try
    {
        settings.Xdr.SqlServer = server;
        settings.Xdr.Database = XdrIdentifiers.Database(parse.GetValue(xdrDatabaseOption) ?? settings.Xdr.Database);
        var table = parse.GetValue(xdrTableOption) ?? settings.Xdr.Table;
        XdrIdentifiers.QuotedTable(table);
        settings.Xdr.Table = table;
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }

    settings.Xdr.TrustServerCertificate = parse.GetValue(xdrTrustOption) || settings.Xdr.TrustServerCertificate;
    settings.Xdr.Mode = settings.DemoMode ? SourceMode.Simulated : SourceMode.Live;
    store.Save(settings);
    Console.WriteLine($"Cortex XDR: {settings.Xdr.SqlServer} / {settings.Xdr.Database} / {settings.Xdr.Table}. A coleta roda a cada {settings.Collection.XdrIntervalMinutes} minutos; use 'nexusctl collect xdr' para testar agora.");
    return 0;
});
root.Subcommands.Add(xdrConfigure);

var xdrGrantScript = new Command("xdr-grant-script", "Gera o T-SQL de leitura da tabela do XDR para o DBA (papel dedicado, reversível).") { sccmAccountOption, revokeOption, outputOption };
xdrGrantScript.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var account = parse.GetValue(sccmAccountOption)!;
    var script = parse.GetValue(revokeOption)
        ? XdrGrantScript.Revoke(settings.Xdr.Database, account)
        : XdrGrantScript.Grant(settings.Xdr.Database, settings.Xdr.Table, account);
    await Write(parse.GetValue(outputOption), script, ct);
    return 0;
});
root.Subcommands.Add(xdrGrantScript);

var xdrGrant = new Command("xdr-grant", "Aplica a concessão de leitura da tabela do XDR com a identidade de quem executa.") { sccmAccountOption };
xdrGrant.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var script = XdrGrantScript.Grant(settings.Xdr.Database, settings.Xdr.Table, parse.GetValue(sccmAccountOption)!);
    var connection = new SqlConnectionStringBuilder(XdrConnectionFactory.BuildConnectionString(settings.Xdr))
    {
        InitialCatalog = "master",
        ApplicationIntent = ApplicationIntent.ReadWrite,
    }.ConnectionString;
    return await ApplyBatches(connection, script, ct);
});
root.Subcommands.Add(xdrGrant);

// network-proxy
var proxyUrlOption = new Option<string>("--url") { Description = "Proxy de saída, ex.: http://proxy.azul.corp:8080." };
var proxyOffOption = new Option<bool>("--off") { Description = "Remove o proxy (conexão direta)." };
var proxyAnonOption = new Option<bool>("--no-credentials") { Description = "Não autenticar no proxy com a conta do serviço." };
var networkProxy = new Command("network-proxy", "Define o proxy de saída usado nas chamadas ao Microsoft Graph e ao Netskope.") { proxyUrlOption, proxyOffOption, proxyAnonOption };
networkProxy.SetAction(parse =>
{
    if (!RequireConfig(out var settings)) return 3;
    if (parse.GetValue(proxyOffOption))
    {
        settings.Network.ProxyUrl = "";
    }
    else if (parse.GetValue(proxyUrlOption) is { Length: > 0 } url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine("Informe o proxy como URL completa, por exemplo http://proxy.azul.corp:8080.");
            return 3;
        }

        settings.Network.ProxyUrl = url;
        settings.Network.ProxyUseDefaultCredentials = !parse.GetValue(proxyAnonOption);
    }

    store.Save(settings);
    Console.WriteLine(settings.Network.ProxyUrl.Length == 0 ? "Conexão direta (sem proxy)." : $"Proxy: {settings.Network.ProxyUrl} ({(settings.Network.ProxyUseDefaultCredentials ? "com a conta do serviço" : "sem credenciais")}). Vale a partir da próxima coleta.");
    return 0;
});
root.Subcommands.Add(networkProxy);

// netskope-configure / netskope-test
var nsTenantOption = new Option<string>("--tenant") { Description = "Tenant do Netskope, ex.: azul.goskope.com." };
var nsPathOption = new Option<string>("--path") { Description = "Caminho do endpoint 'Get Client Data' (padrão /api/v1/clients)." };
var nsPlacementOption = new Option<string>("--token-placement") { Description = "query (API v1: token=...) ou header (API v2: Netskope-Api-Token)." };
var nsPageOption = new Option<int>("--page-size") { Description = "Registros por página (padrão 500).", DefaultValueFactory = _ => 0 };
var nsOffsetOption = new Option<string>("--offset-parameter") { Description = "Nome do parâmetro de deslocamento (skip ou offset)." };
var nsOffOption = new Option<bool>("--off") { Description = "Desliga a coleta do Netskope." };
var nsKeepTokenOption = new Option<bool>("--keep-token") { Description = "Mantém o token já guardado, sem perguntar." };
var netskopeConfigure = new Command("netskope-configure", "Configura a coleta dos clientes (agentes) do Netskope. O token é guardado protegido (DPAPI), nunca em texto.") { nsTenantOption, nsPathOption, nsPlacementOption, nsPageOption, nsOffsetOption, nsOffOption, nsKeepTokenOption };
netskopeConfigure.SetAction(parse =>
{
    if (!RequireConfig(out var settings)) return 3;
    var ns = settings.Netskope;
    if (parse.GetValue(nsOffOption))
    {
        ns.Mode = SourceMode.Disabled;
        store.Save(settings);
        Console.WriteLine("Coleta do Netskope desligada.");
        return 0;
    }

    ns.Tenant = (parse.GetValue(nsTenantOption) ?? ns.Tenant).Trim().Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
    if (string.IsNullOrWhiteSpace(ns.Tenant))
    {
        Console.Error.WriteLine("Informe o tenant com --tenant (ex.: azul.goskope.com).");
        return 3;
    }

    if (parse.GetValue(nsPathOption) is { Length: > 0 } path) ns.ClientsPath = path.StartsWith('/') ? path : "/" + path;
    if (parse.GetValue(nsPlacementOption) is { Length: > 0 } placement)
    {
        if (placement is not ("query" or "header"))
        {
            Console.Error.WriteLine("--token-placement deve ser 'query' ou 'header'.");
            return 3;
        }

        ns.TokenPlacement = placement;
    }

    if (parse.GetValue(nsPageOption) is > 0 and var page) ns.PageSize = Math.Clamp(page, 10, 5000);
    if (parse.GetValue(nsOffsetOption) is { Length: > 0 } offset) ns.OffsetParameter = offset;

    if (!settings.DemoMode && (!parse.GetValue(nsKeepTokenOption) || string.IsNullOrEmpty(ns.ProtectedToken)))
    {
        var token = Environment.GetEnvironmentVariable(NetskopeToken.EnvironmentVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : ReadSecret("Token da API do Netskope (não aparece na tela): ");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("Nenhum token informado.");
            return 3;
        }

        try
        {
            ns.ProtectedToken = new DpapiSecretProtector().Protect(token.Trim());
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message + " Em desenvolvimento use a variável NEXUS_NETSKOPE_TOKEN.");
            return 3;
        }
    }

    ns.Mode = settings.DemoMode ? SourceMode.Simulated : SourceMode.Live;
    store.Save(settings);
    Console.WriteLine($"Netskope: https://{ns.Tenant}{ns.ClientsPath} (token em {ns.TokenPlacement}, {ns.PageSize} por página). Teste com 'nexusctl netskope-test' e colete com 'nexusctl collect netskope --wait'.");
    return 0;
});
root.Subcommands.Add(netskopeConfigure);

var netskopeTest = new Command("netskope-test", "Lê uma página de clientes do Netskope e mostra a estrutura (nomes dos campos, sem valores sensíveis) para conferir o caminho e o mapeamento.");
netskopeTest.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var ns = settings.Netskope;
    string? token;
    try
    {
        token = NetskopeToken.Resolve(ns);
    }
    catch (Exception ex) when (ex is PlatformNotSupportedException or System.Security.Cryptography.CryptographicException)
    {
        Console.Error.WriteLine("Não foi possível ler o token guardado: " + ex.Message);
        return 1;
    }

    if (string.IsNullOrWhiteSpace(ns.Tenant) || token is null)
    {
        Console.Error.WriteLine("Netskope não configurado. Rode: nexusctl netskope-configure --tenant <tenant>.");
        return 3;
    }

    Console.WriteLine("Endereço: " + NetskopeHttpReader.BuildUrl(ns, 0, withToken: false) + $"  (token em {ns.TokenPlacement})");
    using var http = NetworkHttp.Create(settings.Network, TimeSpan.FromSeconds(ns.TimeoutSeconds));
    try
    {
        using var doc = await new NetskopeHttpReader(http, ns, token).ReadRawPageAsync(ct);
        var root = doc.RootElement;
        Console.WriteLine("Resposta: " + root.ValueKind + (root.ValueKind == System.Text.Json.JsonValueKind.Object ? "; campos: " + string.Join(", ", root.EnumerateObject().Select(p => p.Name)) : ""));
        var records = NetskopeParser.Records(root).ToList();
        Console.WriteLine($"Registros na página: {records.Count}");
        if (records.Count > 0)
        {
            Console.WriteLine("Campos do primeiro registro:");
            foreach (var path in FieldPaths(records[0], "", 0)) Console.WriteLine("  " + path);
            var parsed = records.Select(NetskopeParser.Parse).ToList();
            Console.WriteLine($"Reconhecidos pelo Nexus: {parsed.Count(p => p is not null)} de {records.Count}; com nome de host: {parsed.Count(p => p?.HostName is not null)}; com data do último evento: {parsed.Count(p => p?.LastEventAt is not null)}; com número de série: {parsed.Count(p => p?.Serial is not null)}.");
            foreach (var p in parsed.Where(p => p is not null).Take(3)) Console.WriteLine($"  {p!.HostName ?? "(sem nome)"} · {p.Status ?? "—"} · último evento {p.LastEventAt?.ToLocalTime():dd/MM/yyyy HH:mm}");
        }

        return 0;
    }
    catch (NetskopeException ex)
    {
        Console.Error.WriteLine(ex.Error);
        return 1;
    }
});
root.Subcommands.Add(netskopeTest);

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
var sourceArgument = new Argument<string>("fonte") { Description = "all, sccm, ad, intune, entra, xdr, netskope ou inventory.", DefaultValueFactory = _ => "all" };
var waitOption = new Option<bool>("--wait") { Description = "Aguarda o fim da coleta." };
var collect = new Command("collect", "Pede ao Worker uma coleta agora (respeita pausas e limites).") { sourceArgument, waitOption, timeoutOption };
collect.SetAction(async (parse, ct) =>
{
    if (!RequireConfig(out var settings)) return 3;
    var source = parse.GetValue(sourceArgument) switch { "sccm" => "sccm", "ad" => "ad", "intune" => "intune", "entra" => "entra", "xdr" => "xdr", "netskope" => "netskope", "inventory" => "inventory", _ => "all" };
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

static string ReadSecret(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? "";
    }

    var buffer = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length > 0) buffer.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
        {
            buffer.Append(key.KeyChar);
        }
    }

    Console.Error.WriteLine();
    return buffer.ToString();
}

static IEnumerable<string> FieldPaths(System.Text.Json.JsonElement element, string prefix, int depth)
{
    if (element.ValueKind != System.Text.Json.JsonValueKind.Object || depth > 2)
    {
        yield break;
    }

    foreach (var property in element.EnumerateObject())
    {
        var path = prefix + property.Name;
        yield return $"{path} ({property.Value.ValueKind})";
        if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var child in FieldPaths(property.Value, path + ".", depth + 1)) yield return child;
        }
        else if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Array && property.Value.GetArrayLength() > 0)
        {
            foreach (var child in FieldPaths(property.Value[0], path + "[].", depth + 1)) yield return child;
        }
    }
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
