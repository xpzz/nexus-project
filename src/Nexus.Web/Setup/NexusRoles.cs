using System.Security.Claims;

namespace Nexus.Web.Setup;

/// <summary>
/// Application roles (ADR-0008). They are Entra ID app roles of the "Azul Nexus – Web" registration; the Entra token carries them in the <c>roles</c> claim.
/// Without SSO (open access) visitors are readers, and only the setup principal (server, or one-time code) holds every role.
/// </summary>
public static class NexusRoles
{
    public const string Reader = "Nexus.Leitura";
    public const string Analyst = "Nexus.Analista";
    public const string IntegrationAdmin = "Nexus.AdminIntegracao";
    public const string Auditor = "Nexus.Auditoria";

    public static readonly string[] All = [Reader, Analyst, IntegrationAdmin, Auditor];

    public static string Title(string role) => role switch
    {
        Reader => "Leitura",
        Analyst => "Analista",
        IntegrationAdmin => "Administrador de integração",
        Auditor => "Auditor",
        _ => role,
    };
}

/// <summary>What the current user may do. One place decides, pages and endpoints only ask.</summary>
public sealed record CurrentAccess(string Actor, bool IsAuthenticated, IReadOnlySet<string> Roles, bool IsSetup)
{
    public static CurrentAccess Anonymous { get; } = new("anônimo", false, new HashSet<string>(), false);

    private bool Has(params string[] roles) => IsSetup || roles.Any(Roles.Contains);

    /// <summary>Everyone who can open the site reads the inventory.</summary>
    public bool CanRead => true;

    /// <summary>Sees personal data of BYOD owners (user names); readers see them masked (LGPD).</summary>
    public bool CanSeePersonalData => Has(NexusRoles.Analyst, NexusRoles.IntegrationAdmin, NexusRoles.Auditor);

    /// <summary>Saves named filters, records exceptions and exports data.</summary>
    public bool CanAnalyze => Has(NexusRoles.Analyst, NexusRoles.IntegrationAdmin);

    public bool CanExport => CanAnalyze;

    /// <summary>Runs collections by hand and changes how connectors behave.</summary>
    public bool CanOperate => Has(NexusRoles.IntegrationAdmin);

    /// <summary>Reads the administrative audit trail. Administrators do not read their own trail: that is the auditor's job.</summary>
    public bool CanAudit => Has(NexusRoles.Auditor);

    public static CurrentAccess From(ClaimsPrincipal? user, string? remote)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return Anonymous with { Actor = remote is { Length: > 0 } ? "visitante@" + remote : "anônimo" };
        }

        var roles = user.Claims.Where(c => c.Type is ClaimTypes.Role or "roles").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = user.FindFirst("preferred_username")?.Value ?? user.FindFirst(ClaimTypes.Upn)?.Value ?? user.Identity.Name ?? "autenticado";
        return new CurrentAccess(name, true, roles, roles.Contains(SetupAccessMiddleware.SetupRole));
    }
}

public static class PersonalData
{
    /// <summary>ana.silva@azul.com becomes a*******@azul.com. Keeps enough to tell two people apart in a list without exposing who they are.</summary>
    public static string Mask(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return "—";
        }

        var at = user.IndexOf('@');
        var local = at > 0 ? user[..at] : user;
        var domain = at > 0 ? user[at..] : "";
        return local[0] + new string('•', Math.Clamp(local.Length - 1, 3, 8)) + domain;
    }
}
