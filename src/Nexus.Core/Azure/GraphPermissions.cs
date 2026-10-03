namespace Nexus.Core.Azure;

public sealed record GraphPermission(string Name, string Purpose, int Phase, bool Optional = false, string? Probe = null);

/// <summary>Application permissions of the "Azul Nexus – Coletor" registration (SPEC §4.5).</summary>
public static class GraphPermissions
{
    public static readonly IReadOnlyList<GraphPermission> Collector =
    [
        new("DeviceManagementManagedDevices.Read.All", "Dispositivos gerenciados, sincronização e compliance por dispositivo", 1, Probe: "/deviceManagement/managedDevices?$top=1&$select=id"),
        new("Device.Read.All", "Dispositivos do Entra: deviceId, trustType e atividade", 1, Probe: "/devices?$top=1&$select=id"),
        new("User.Read.All", "Usuários, departamento e conta habilitada", 1, Probe: "/users?$top=1&$select=id"),
        new("GroupMember.Read.All", "Grupos usados em escopos e no gerenciamento esperado", 1, Probe: "/groups?$top=1&$select=id"),
        new("DeviceManagementConfiguration.Read.All", "Políticas de compliance e configuração e seus estados", 2, Probe: "/deviceManagement/deviceCompliancePolicies?$top=1&$select=id"),
        new("DeviceManagementApps.Read.All", "Aplicativos e MAM (políticas e registros de proteção)", 2, Probe: "/deviceAppManagement/mobileApps?$top=1&$select=id"),
        new("DeviceManagementServiceConfig.Read.All", "Enrollment, Autopilot, APNs, ADE e VPP", 2, Probe: "/deviceManagement/applePushNotificationCertificate"),
        new("Organization.Read.All", "Licenças contratadas, para marcar a disponibilidade dos KPIs", 2, Optional: true, Probe: "/organization?$select=id"),
    ];

    /// <summary>Permissions required by the active modules, i.e. up to the given phase.</summary>
    public static IReadOnlyList<GraphPermission> RequiredUpTo(int phase, bool includeOptional = false) =>
        Collector.Where(p => p.Phase <= phase && (includeOptional || !p.Optional)).ToList();

    /// <summary>Compares the "roles" claim of the app token with the required permissions.</summary>
    public static IReadOnlyList<GraphPermission> Missing(IEnumerable<string> grantedRoles, IEnumerable<GraphPermission> required)
    {
        var granted = new HashSet<string>(grantedRoles, StringComparer.OrdinalIgnoreCase);
        return required.Where(p => !granted.Contains(p.Name)).ToList();
    }
}
