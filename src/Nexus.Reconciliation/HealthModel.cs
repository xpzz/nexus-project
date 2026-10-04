using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

public enum Priority
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3,
}

/// <summary>A pending rule (SPEC v1 §6). Rules that need data the Nexus does not collect yet are listed as unavailable, never as zero.</summary>
public sealed record Rule(string Id, string Title, Priority Priority, string Owner, string Action, string? MissingData = null)
{
    public bool Available => MissingData is null;

    public int Weight => Priority switch { Priority.Critical => 40, Priority.High => 20, Priority.Medium => 8, _ => 3 };
}

public static class Groups
{
    public const string Computers = "pc", Servers = "srv", CorpMobile = "celc", ByodMobile = "celb", ByodComputers = "pcb", External = "ext";
    public static readonly string[] All = [Computers, Servers, CorpMobile, ByodMobile, ByodComputers, External];

    public static string Title(string g) => g switch
    {
        Computers => "Computadores",
        Servers => "Servidores",
        CorpMobile => "Celulares corporativos",
        ByodMobile => "Celulares BYOD",
        ByodComputers => "Computadores BYOD",
        External => "Externos",
        _ => g,
    };

    public static string Description(string g) => g switch
    {
        Computers => "Notebooks e desktops corporativos, Windows e macOS",
        Servers => "Windows Server",
        CorpMobile => "iPhone e Android da empresa",
        ByodMobile => "Aparelhos pessoais com acesso corporativo",
        ByodComputers => "Windows e Mac pessoais",
        External => "Convidados e prestadores registrados no Entra ID",
        _ => "",
    };

    /// <summary>What "managed" means for the group (SPEC v1 §4.2).</summary>
    public static string Expected(string g) => g switch
    {
        Computers => "Co-gestão (SCCM + Intune MDM); macOS só Intune",
        Servers => "Cliente SCCM; Intune MDM não se aplica",
        CorpMobile => "Intune MDM",
        ByodMobile => "MDM, MAM ou ambos",
        ByodComputers => "MAM",
        External => "Registrado não é gerenciado",
        _ => "",
    };
}

public static class Management
{
    public const string CoManaged = "Co-gestão", OnlySccm = "Só SCCM", OnlyIntune = "Só Intune", OnlyMam = "Só MAM", None = "Sem gestão";
    public static readonly string[] All = [CoManaged, OnlySccm, OnlyIntune, OnlyMam, None];
}

public static class States
{
    public const string Healthy = "Saudável", Attention = "Atenção", Risk = "Risco", Stale = "Desatualizado";
    public static readonly string[] All = [Healthy, Attention, Risk, Stale];
}

/// <summary>Pure classification of a reconciled asset: group, management mode, issues, health score and state (SPEC v1 §4–6).</summary>
public static class HealthModel
{
    public static readonly IReadOnlyList<Rule> Rules =
    [
        new("byodnoprot", "BYOD sem proteção comprovada (sem MDM nem MAM)", Priority.Critical, "Segurança", "Exigir proteção de apps no Acesso Condicional", "Proteção de aplicativos (MAM) ainda não é coletada"),
        new("rooted", "Root ou jailbreak detectado", Priority.Critical, "Segurança", "Bloquear acesso e notificar o usuário", "Sinal de integridade do dispositivo ainda não é coletado"),
        new("noclient", "Cliente SCCM ausente (Windows corporativo ativo)", Priority.High, "Operações de TI", "Reinstalar via client push"),
        new("nomdm", "Windows corporativo sem Intune MDM", Priority.High, "Endpoint", "Habilitar auto-enrollment da co-gestão"),
        new("nobitlocker", "BitLocker desligado", Priority.High, "Segurança", "Aplicar perfil de criptografia", "Estado de criptografia ainda não é coletado"),
        new("eol", "Windows 10 fora de suporte (desde out/2025)", Priority.High, "Endpoint", "Migrar para Windows 11 ou registrar ESU"),
        new("nomgr", "Corporativo sem gestão", Priority.High, "Operações de TI", "Reinserir na gestão ou dar baixa"),
        new("noncomp", "Não conforme no Intune", Priority.Medium, "Endpoint", "Revisar políticas falhando"),
        new("cleval", "Falha na avaliação do cliente SCCM", Priority.Medium, "Operações de TI", "Executar reparo do cliente", "Avaliação do cliente SCCM ainda não é coletada"),
        new("patch", "Atualizações atrasadas (> 60 dias)", Priority.Medium, "Endpoint", "Verificar anel e janela de manutenção", "Estado de atualizações ainda não é coletado"),
        new("nopolicy", "Conforme sem política atribuída", Priority.Medium, "Endpoint", "Revisar atribuição de grupos", "Políticas atribuídas ainda não são coletadas"),
        new("stalecomm", "SCCM mudo, Intune ativo", Priority.Medium, "Operações de TI", "Investigar saúde do cliente"),
        new("oslow", "Sistema abaixo do mínimo", Priority.Medium, "Endpoint", "Notificar o usuário para atualizar", "Versão mínima exigida ainda não é configurada"),
        new("userdis", "Equipamento de usuário desabilitado", Priority.Medium, "RH + TI", "Validar devolução ou baixa", "Estado da conta do usuário ainda não é coletado"),
        new("review", "Reconciliação a revisar", Priority.Medium, "Operações de TI", "Revisar na fila"),
        new("stale", "Sem comunicação", Priority.Low, "Operações de TI", "Confirmar se o equipamento existe"),
    ];

    public static Rule RuleOf(string id) => Rules.First(r => r.Id == id);

    public static string GroupOf(Asset a)
    {
        var mobile = a.Platform is "Android" or "iOS";
        var entraOnly = a is { InEntra: true, InIntune: false, InSccm: false, InAd: false };
        if (entraOnly)
        {
            return Groups.External;
        }

        if (a.Ownership == "Personal")
        {
            return mobile ? Groups.ByodMobile : Groups.ByodComputers;
        }

        if (mobile)
        {
            return Groups.CorpMobile;
        }

        return a.Platform == "WindowsServer" ? Groups.Servers : Groups.Computers;
    }

    public static string ManagementOf(Asset a) => a.Coverage switch
    {
        "Both" => Management.CoManaged,
        "OnlySccm" => Management.OnlySccm,
        "OnlyIntune" => Management.OnlyIntune,
        _ => Management.None,
    };

    /// <summary>True when the device has the management its group expects (SPEC v1 §4.2). Externals and BYOD computers have no management requirement here.</summary>
    public static bool MeetsExpectedManagement(Asset a, string group) => group switch
    {
        Groups.Computers => a.Platform == "macOS" ? a.IntuneChannel == "Mdm" : a.Coverage == "Both",
        Groups.Servers => a.SccmClient,
        Groups.CorpMobile or Groups.ByodMobile => a.IntuneChannel == "Mdm",
        _ => true,
    };

    /// <summary>Windows 10 builds are 10.0.19xxx; Windows 11 starts at 10.0.22000.</summary>
    public static bool IsWindows10(Asset a)
    {
        if (a.Platform != "WindowsClient")
        {
            return false;
        }

        if (a.OsVersion is { } v && v.StartsWith("10.0.", StringComparison.Ordinal) && int.TryParse(v.AsSpan(5).ToString().Split('.')[0], out var build))
        {
            return build < 22000;
        }

        return a.OperatingSystem?.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static IReadOnlyList<string> Issues(Asset a, string group, SourceAvailability sources)
    {
        var issues = new List<string>();
        var corporate = a.Ownership == "Corporate";
        var windows = a.Platform is "WindowsClient" or "WindowsServer";
        if (corporate && a.IsActive && windows && sources.Sccm)
        {
            if (a is { InSccm: true, SccmClient: false } && a.SccmHealth != "Obsolete")
            {
                issues.Add("noclient");
            }
        }

        if (group == Groups.Computers && a is { IsActive: true, Platform: "WindowsClient" } && sources.Intune && a.IntuneChannel != "Mdm")
        {
            issues.Add("nomdm");
        }

        if (corporate && a.IsActive && IsWindows10(a))
        {
            issues.Add("eol");
        }

        if (corporate && a.IsActive && a.Coverage == "Neither" && group is Groups.Computers or Groups.Servers)
        {
            issues.Add("nomgr");
        }

        if (a is { IsActive: true, IntuneChannel: "Mdm" } && string.Equals(a.ComplianceState, "noncompliant", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("noncomp");
        }

        if (a is { InSccm: true, SccmClient: true, IsActive: true } && a.SccmLastSeenAt is { } sccmSeen && a.IntuneLastSyncAt is { } sync
            && sync - sccmSeen > TimeSpan.FromDays(23))
        {
            issues.Add("stalecomm");
        }

        if (a.NeedsReview)
        {
            issues.Add("review");
        }

        if (!a.IsActive)
        {
            issues.Add("stale");
        }

        return issues;
    }

    public static int Score(IEnumerable<string> issues) => Math.Max(0, 100 - issues.Select(RuleOf).Where(r => r.Id != "stale").Sum(r => r.Weight));

    public static string StateOf(Asset a, IReadOnlyCollection<string> issues)
    {
        if (!a.IsActive)
        {
            return States.Stale;
        }

        var worst = issues.Where(i => i != "stale").Select(i => RuleOf(i).Priority).DefaultIfEmpty(Priority.Low).Min();
        var any = issues.Any(i => i != "stale");
        return !any ? States.Healthy : worst <= Priority.High ? States.Risk : States.Attention;
    }
}

/// <summary>An asset with everything the screens need, computed once.</summary>
public sealed record AssetView(Asset Asset, string Group, string Management, string State, int Score, IReadOnlyList<string> Issues, bool MeetsExpected)
{
    public static AssetView From(Asset a, SourceAvailability sources)
    {
        var group = HealthModel.GroupOf(a);
        var issues = HealthModel.Issues(a, group, sources);
        return new AssetView(a, group, HealthModel.ManagementOf(a), HealthModel.StateOf(a, issues), HealthModel.Score(issues), issues, HealthModel.MeetsExpectedManagement(a, group));
    }
}
