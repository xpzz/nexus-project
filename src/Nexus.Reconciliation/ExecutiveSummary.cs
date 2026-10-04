using Nexus.Core.Configuration;
using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

/// <summary>Health of one data source, built from its collection jobs. Optional sources that were never configured are hidden, not shown as failures.</summary>
public sealed record SourceHealth(string Key, string Label, bool Optional, string Status, string Text, string Detail, DateTimeOffset? LastSuccessAt, int? Records, IReadOnlyList<JobSummary> Jobs)
{
    public const string Ok = "ok", Late = "late", Failed = "failed", Partial = "partial", Never = "never", NotConfigured = "off";

    public bool Usable => Status is Ok or Late or Partial;

    /// <summary>The source needs attention: its data is old or its collection fails.</summary>
    public bool NeedsAttention => Status is Late or Failed or Partial or Never;
}

public static class SourceHealthBuilder
{
    public static readonly (string Key, string Label, bool Optional, string[] Jobs)[] Sources =
    [
        ("sccm", "SCCM", false, ["sccm.devices"]),
        ("ad", "Active Directory", false, ["ad.computers"]),
        ("intune", "Intune", false, ["intune.devices", "intune.policies", "intune.mam"]),
        ("entra", "Entra ID", false, ["entra.devices", "entra.users"]),
        ("governance", "Governança Microsoft (APP, Acesso Condicional, sign-ins)", true, ["intune.apppolicies", "entra.ca", "entra.signins"]),
        ("xdr", "Cortex XDR", true, ["xdr.endpoints"]),
        ("netskope", "Netskope", true, ["netskope.clients"]),
    ];

    /// <summary>A collection is late when it should have run more than this long ago (the worker may be stopped or postponing).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(20);

    public static string JobLabel(string job) => job switch
    {
        "sccm.devices" => "Dispositivos do SCCM",
        "ad.computers" => "Computadores do AD",
        "intune.devices" => "Dispositivos do Intune",
        "entra.devices" => "Dispositivos do Entra ID",
        "xdr.endpoints" => "Endpoints do Cortex XDR",
        "netskope.clients" => "Clientes do Netskope",
        "intune.mam" => "Proteção de aplicativos (MAM)",
        "entra.users" => "Usuários do Entra ID",
        "intune.policies" => "Políticas do Intune",
        "intune.apppolicies" => "Políticas de proteção de apps e configuração do Edge",
        "entra.ca" => "Políticas de Acesso Condicional",
        "entra.signins" => "Acessos ao Microsoft 365 (sign-ins)",
        "inventory.reconcile" => "Reconciliação",
        _ => job,
    };

    public static IReadOnlyList<SourceHealth> Build(IReadOnlyList<JobSummary> allJobs, DateTimeOffset now)
    {
        var result = new List<SourceHealth>();
        foreach (var (key, label, optional, names) in Sources)
        {
            var jobs = allJobs.Where(j => names.Contains(j.Name)).ToList();
            if (optional && jobs.All(j => j.Status is null or "Não configurada" && j.LastSuccessAt is null))
            {
                continue;
            }

            var succeeded = jobs.Where(j => j.LastSuccessAt is not null).ToList();
            // Records of the device collection only: summing users, policies and MAM into one number would suggest a device count that does not exist.
            var records = jobs.FirstOrDefault(j => j.Name == names[0] && j.LastSuccessAt is not null)?.Records;
            if (succeeded.Count == 0)
            {
                var error = jobs.FirstOrDefault(j => !string.IsNullOrEmpty(j.Error))?.Error;
                var failed = jobs.Any(j => j.Status == "Falhou");
                result.Add(new SourceHealth(key, label, optional, failed ? SourceHealth.Failed : SourceHealth.Never, failed ? "com falha" : "sem coleta",
                    error ?? "Ainda não coletou. Peça uma coleta em Operações.", null, null, jobs));
                continue;
            }

            var oldest = succeeded.Min(j => j.LastSuccessAt)!.Value;
            var failedJobs = jobs.Where(j => j.Status == "Falhou").ToList();
            var never = jobs.Where(j => j.LastSuccessAt is null && j.Status is not "Não configurada" and not null).ToList();
            var overdue = jobs.Where(j => j.NextRunAt is { } due && now - due > Grace && j.Status is not "Falhou").ToList();
            if (failedJobs.Count > 0)
            {
                result.Add(new SourceHealth(key, label, optional, SourceHealth.Failed, "coleta com falha", string.Join(" | ", failedJobs.Select(j => $"{JobLabel(j.Name)}: {j.Error}")), oldest, records, jobs));
            }
            else if (overdue.Count > 0)
            {
                result.Add(new SourceHealth(key, label, optional, SourceHealth.Late, "atrasada", "Coleta atrasada: " + string.Join(", ", overdue.Select(j => JobLabel(j.Name))) + ". Confira se o Worker está em execução.", oldest, records, jobs));
            }
            else if (never.Count > 0)
            {
                result.Add(new SourceHealth(key, label, optional, SourceHealth.Partial, "parcial", "Ainda sem coleta de: " + string.Join(", ", never.Select(j => JobLabel(j.Name))), oldest, records, jobs));
            }
            else
            {
                result.Add(new SourceHealth(key, label, optional, SourceHealth.Ok, "em dia", "Última coleta concluída sem erro.", oldest, records, jobs));
            }
        }

        return result;
    }
}

/// <summary>A headline number with everything needed to trust it: what it means, which population and window it uses, how fresh the data is and where the list behind it is.</summary>
public sealed record ExecKpi(string Key, string Label, string Definition, int Count, int Denominator, string Population, string Window, string Link, string? Caveat)
{
    public double? Percent => Denominator > 0 ? Math.Round(100.0 * Count / Denominator, 1) : null;
}

public enum QueueStatus { HasItems, Zero, NoData, Failed, Unavailable }

/// <summary>An item of the attention queue. Zero occurrences, missing data, a failed collection and a feature not built yet are four different things.</summary>
public sealed record AttentionItem(string Key, string Reason, string Why, QueueStatus Status, int Count, string? Link, string Note);

public sealed record GovernanceRow(string Concept, string Meaning, int Count, int Denominator, string Population, string? Link, string? Unavailable)
{
    public double? Percent => Unavailable is null && Denominator > 0 ? Math.Round(100.0 * Count / Denominator, 1) : null;
}

public sealed record ExecutiveReport(
    DateTimeOffset AsOf, int UniqueAssets, int Evaluated, int Decommissioned, IReadOnlyList<ExecKpi> Kpis,
    IReadOnlyList<SourceHealth> Sources, IReadOnlyDictionary<string, int> States, IReadOnlyDictionary<string, int> Types,
    IReadOnlyList<AttentionItem> Queue, IReadOnlyList<GovernanceRow> Governance, EvidenceThresholds Thresholds, IReadOnlyList<string> MissingSources);

public static class ExecutiveSummary
{
    public static ExecutiveReport Build(InventorySnapshot s)
    {
        var views = s.Views;
        var now = s.LoadedAt;
        var evaluated = views.Where(v => v.Asset.OperationalState != OperationalStates.Decommissioned).ToList();
        var health = SourceHealthBuilder.Build(s.Jobs, now);
        var core = health.Where(h => !h.Optional).ToList();
        var missing = SourceHealthBuilder.Sources.Where(x => !x.Optional).Select(x => x.Label).Where(l => health.FirstOrDefault(h => h.Label == l) is not { Usable: true }).ToList();
        var caveat = missing.Count == 0 ? null : "Calculado sem dados de " + string.Join(", ", missing) + ".";
        var th = s.Evidence.Default;
        var window = $"confirmado ≤ {th.ConfirmedDays} dias, provável ≤ {th.ProbableDays}, observação ≤ {th.NoRecentDays} (por tipo de ativo)";
        int Count(string state) => evaluated.Count(v => v.Asset.OperationalState == state);

        var population = $"{Fmt(evaluated.Count)} ativos únicos avaliados (exclui descomissionados)";
        var kpis = new List<ExecKpi>
        {
            new("confirmed", "Ativos confirmados", "Duas ou mais ferramentas independentes reportaram dentro da janela de confirmação, ao menos uma delas no equipamento.", Count(OperationalStates.Confirmed), evaluated.Count,
                population, window, Link(OperationalStates.Confirmed), caveat),
            new("probable", "Ativos prováveis", "Uma ferramenta que roda no equipamento reportou dentro da janela de probabilidade, ou duas fontes de identidade concordam. Falta confirmação independente.", Count(OperationalStates.Probable), evaluated.Count,
                population, window, Link(OperationalStates.Probable), caveat),
            new("norecent", "Sem evidência recente", "Há relato dentro da janela de observação, mas nada recente o bastante para afirmar uso; inclui os inativos sem nenhum relato na janela.", Count(OperationalStates.NoRecent) + Count(OperationalStates.Inactive), evaluated.Count,
                population, window, "/inventario?oper=NoRecentEvidence,Inactive", caveat),
            new("conflicts", "Conflitos de identificação", "Ativos cuja identidade é ambígua (serial duplicado, clone, nome em mais de um objeto). Não foram mesclados e precisam de revisão.", Count(OperationalStates.Conflicting), evaluated.Count,
                population, "estado atual da reconciliação", Link(OperationalStates.Conflicting), caveat),
        };

        var states = OperationalStates.All.ToDictionary(x => x, x => views.Count(v => v.Asset.OperationalState == x));
        var types = AssetTypes.All.ToDictionary(x => x, x => evaluated.Count(v => v.Asset.AssetType == x));
        return new ExecutiveReport(now, views.Count, evaluated.Count, views.Count - evaluated.Count, kpis, health, states, types, Queue(s, evaluated, health), Governance(s, evaluated), th, missing);

        static string Link(string state) => "/inventario?oper=" + state;
        static string Fmt(int n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
    }

    private static IReadOnlyList<AttentionItem> Queue(InventorySnapshot s, List<AssetView> evaluated, IReadOnlyList<SourceHealth> health)
    {
        var items = new List<AttentionItem>();
        var src = s.Sources;
        var active = evaluated.Where(v => OperationalStates.IsActive(v.Asset.ActivityLevel)).ToList();

        // 1. Recent assets with no management identified.
        if (!(src.Sccm || src.Intune))
        {
            items.Add(new("nomgmt", "Ativos recentes sem gerenciamento identificado", "Equipamentos com evidência de uso que SCCM e Intune não gerenciam.", QueueStatus.NoData, 0, null, "SCCM e Intune ainda sem coleta: não há como saber quem é gerenciado."));
        }
        else
        {
            var n = active.Count(v => v.Management == Management.None && v.Asset.Ownership == "Corporate");
            items.Add(new("nomgmt", "Ativos recentes sem gerenciamento identificado", "Corporativos com evidência de uso que não aparecem no SCCM nem no Intune MDM.", n == 0 ? QueueStatus.Zero : QueueStatus.HasItems, n,
                "/inventario?oper=ConfirmedActive,ProbableActive&gestao=" + Uri.EscapeDataString(Management.None) + "&propriedade=Corporativo", n == 0 ? "Nenhum ativo recente fora da gestão." : "Reinserir na gestão ou dar baixa."));
        }

        // 2. Identity conflicts.
        var conflicts = evaluated.Count(v => v.Asset.OperationalState == OperationalStates.Conflicting);
        items.Add(new("conflict", "Correlações conflitantes", "Registros que podem ser o mesmo equipamento e não foram mesclados.", conflicts == 0 ? QueueStatus.Zero : QueueStatus.HasItems, conflicts,
            "/qualidade", conflicts == 0 ? "Nenhum conflito de identidade." : "Revisar na página de qualidade dos dados."));

        // 3. Personal devices with corporate data and no proven protection.
        if (!(src.Intune && src.Mam))
        {
            items.Add(new("byodgap", "BYOD sem proteção comprovada", "Aparelhos pessoais ativos sem MDM nem proteção de aplicativos (MAM).", QueueStatus.NoData, 0, null, "Intune ou MAM ainda sem coleta."));
        }
        else
        {
            var n = active.Count(v => v.Group is Groups.ByodMobile or Groups.ByodComputers && v.Issues.Contains("byodnoprot"));
            items.Add(new("byodgap", "BYOD sem proteção comprovada", "Aparelhos pessoais ativos sem MDM nem MAM. O acesso corporativo observado ainda não é coletado (sign-ins), então a lista usa o registro no Entra/Intune.", n == 0 ? QueueStatus.Zero : QueueStatus.HasItems, n,
                "/inventario?pendencia=byodnoprot", n == 0 ? "Nenhum BYOD ativo sem proteção." : "Exigir proteção de apps no Acesso Condicional."));
        }

        items.Add(new("access", "Acesso corporativo observado em dispositivos sem proteção", "Cruza sign-ins do Microsoft 365 com a postura do dispositivo.", QueueStatus.Unavailable, 0, null, "Funcionalidade não disponível: a coleta de sign-ins do Entra ID ainda não foi implementada ou habilitada."));

        // 4. Sources whose collection is late or failing.
        var bad = health.Where(h => h.NeedsAttention).ToList();
        items.Add(new("sources", "Fontes com coleta atrasada ou com falha", "Dados antigos distorcem a classificação de atividade.", bad.Count == 0 ? QueueStatus.Zero : bad.Any(h => h.Status is SourceHealth.Failed) ? QueueStatus.Failed : QueueStatus.HasItems, bad.Count,
            "/operacao", bad.Count == 0 ? "Todas as fontes em dia." : string.Join("; ", bad.Select(h => $"{h.Label}: {h.Text}"))));

        // 5. Candidates for retirement.
        var candidates = evaluated.Count(v => v.Asset.DecommissionCandidate);
        items.Add(new("retire", "Candidatos à inativação", "Sem nenhum relato há mais tempo que o limite de descomissionamento do tipo.", candidates == 0 ? QueueStatus.Zero : QueueStatus.HasItems, candidates,
            "/inventario?oper=Inactive&flag=candidato", candidates == 0 ? "Nenhum candidato." : "Confirmar se o equipamento ainda existe."));

        return items;
    }

    private static IReadOnlyList<GovernanceRow> Governance(InventorySnapshot s, List<AssetView> evaluated)
    {
        var src = s.Sources;
        var active = evaluated.Where(v => OperationalStates.IsActive(v.Asset.ActivityLevel)).ToList();
        var user = active.Where(v => v.Asset.AssetType is not AssetTypes.Server).ToList();
        var mdm = active.Where(v => v.Asset.IntuneChannel == "Mdm").ToList();
        var byod = active.Where(v => v.Group is Groups.ByodMobile or Groups.ByodComputers).ToList();
        string? NeedIntune(bool ok, string what) => ok ? null : $"{what} ainda sem coleta.";

        return
        [
            new("Registrado no Entra", "O dispositivo existe no Entra ID. Não implica gerenciamento.", user.Count(v => v.Asset.InEntra), user.Count, "ativos recentes que não são servidores", "/inventario?oper=ConfirmedActive,ProbableActive&flag=entra", NeedIntune(src.Entra, "Entra ID")),
            new("Gerenciado por MDM", "Intune com agente MDM (co-gestão ou só Intune). Tenant attach não conta.", user.Count(v => v.Asset.IntuneChannel == "Mdm"), user.Count, "ativos recentes que não são servidores", "/inventario?oper=ConfirmedActive,ProbableActive&flag=mdm", NeedIntune(src.Intune, "Intune")),
            new("Compliant no Intune", "Estado de conformidade do Intune igual a conforme.", mdm.Count(v => string.Equals(v.Asset.ComplianceState, "compliant", StringComparison.OrdinalIgnoreCase)), mdm.Count, "ativos recentes com MDM", "/inventario?oper=ConfirmedActive,ProbableActive&flag=mdm,naoconforme", NeedIntune(src.Intune, "Intune")),
            new("Protegido por MAM", "Há registro de proteção de aplicativos. Protege apenas os aplicativos compatíveis, não o aparelho.", byod.Count(v => v.Asset.HasMam), byod.Count, "BYOD ativos", "/byod", NeedIntune(src.Mam, "Proteção de aplicativos (MAM)")),
            new("Com agente de segurança", "Agente do Cortex XDR conectado.", active.Count(v => v.Asset.InXdr && Reconciler.IsXdrConnected(v.Asset.XdrStatus) && v.Asset.AssetType is not AssetTypes.Phone and not AssetTypes.Tablet), active.Count(v => v.Asset.AssetType is not AssetTypes.Phone and not AssetTypes.Tablet), "ativos recentes que não são celulares nem tablets", "/inventario?funil=xdr-sem-agente", NeedIntune(src.Xdr, "Cortex XDR")),
            new("Acesso permitido por Acesso Condicional", "Resultado das políticas de Acesso Condicional avaliado por dispositivo.", 0, 0, "não disponível", null, "Funcionalidade não disponível: a coleta de Acesso Condicional e de sign-ins ainda não foi implementada ou habilitada."),
        ];
    }
}
