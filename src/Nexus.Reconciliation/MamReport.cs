using System.Text.Json;
using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

public sealed record ControlView(ControlResult Result, EvidenceLevel Level);

public sealed record PolicyView(AppProtectionPolicyRecord Policy, IReadOnlyList<string> Apps, IReadOnlyList<ControlView> Controls, int AppliedRegistrations, int ConfiguredCount, bool Stale);

public sealed record EdgeConfigView(AppConfigRecord Config, EdgeListInfo Lists, int EdgeRegistrations, int EdgeRegistrationsWithPolicy);

public sealed record UserGap(string UserId, string? Upn, string Reason, string Platforms, DateTimeOffset? LastAccessAt, ProtectionException? Exception);

/// <summary>Users covered by an app protection policy, by the unit that matters: people (not devices, not computers).</summary>
public sealed record UserCoverage(int Eligible, int Covered, int RegisteredWithoutPolicy, int Uncovered, int WithException, IReadOnlyList<UserGap> Gaps)
{
    public double? Percent => Eligible == 0 ? null : Math.Round(100.0 * Covered / Eligible, 1);
}

/// <summary>Protected app instances: registrations of an app on a device for a user.</summary>
public sealed record AppInstances(int Total, int WithPolicy, int WithoutPolicy, IReadOnlyDictionary<string, int> ByPlatform, IReadOnlyList<(string App, int Count, int WithPolicy)> TopApps)
{
    public double? Percent => Total == 0 ? null : Math.Round(100.0 * WithPolicy / Total, 1);
}

public sealed record DeviceModes(string Label, int MdmAndMam, int MdmOnly, int MamOnly, int Neither)
{
    public int Total => MdmAndMam + MdmOnly + MamOnly + Neither;
}

public enum CaRequirementState { Enforced, ReportOnly, Absent }

public sealed record CaRequirement(CaRequirementState State, IReadOnlyList<ConditionalAccessRecord> Policies);

public sealed record AccessGaps(int DevicesWithoutProtection, int MobileAccessWithoutDevice, IReadOnlyList<AssetView> Devices, IReadOnlyList<AccessEvidenceRecord> Anonymous);

public sealed record ByodPlatform(string Platform, int Devices, int Mdm, int Mam, int MdmAndMam, int NoProtection, int WithAccessAndNoProtection);

public sealed record MamReport(
    bool PoliciesCollected, bool RegistrationsCollected, bool ConditionalAccessCollected, bool SignInsCollected,
    UserCoverage Users, AppInstances Apps, IReadOnlyList<DeviceModes> Devices, IReadOnlyList<PolicyView> Policies, IReadOnlyList<EdgeConfigView> Edge,
    CaRequirement ConditionalAccess, AccessGaps Access, IReadOnlyList<ProtectionException> Exceptions, IReadOnlyList<ByodPlatform> Byod)
{
    public static MamReport Build(InventorySnapshot s)
    {
        var data = s.Protection;
        var src = s.Sources;
        var now = s.LoadedAt;
        var regs = data.Registrations;
        var exceptions = data.Exceptions.Where(e => e.ExpiresAt is null || e.ExpiresAt > now).ToList();
        var users = data.Users.ToDictionary(u => u.Id, u => u, StringComparer.OrdinalIgnoreCase);

        string? Upn(string? id) => id is not null && users.TryGetValue(id, out var u) ? u.UserPrincipalName : null;
        ProtectionException? ExceptionOf(string? userId, string control = "mam") =>
            userId is null ? null : exceptions.FirstOrDefault(e => e.Control == control && e.SubjectKind == "user" && string.Equals(e.SubjectId, userId, StringComparison.OrdinalIgnoreCase));

        // ---- policies and controls, with the proof that each one reaches devices
        var policyViews = new List<PolicyView>();
        foreach (var p in data.Policies.OrderBy(x => x.Platform).ThenBy(x => x.Name))
        {
            var applied = regs.Count(r => (r.AppliedPolicies ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries).Contains(p.Name));
            var settings = SettingsOf(p.SettingsJson);
            var controls = ProtectionControls.Evaluate(p.Platform, settings).Select(c => new ControlView(c, EvidenceLevels.Of(c.Setting, p.IsAssigned, applied, src.Mam))).ToList();
            policyViews.Add(new PolicyView(p, Strings(p.AppsJson), controls, applied, controls.Count(c => c.Result.Setting == ControlSetting.Configured),
                p.LastModifiedAt is { } at && (now - at).TotalDays > data.Settings.StalePolicyDays));
        }

        // ---- Edge governance: which lists exist, how full, and how many Edge registrations prove the policy reached devices
        var edge = new List<EdgeConfigView>();
        foreach (var c in data.Configs.OrderBy(x => x.Platform).ThenBy(x => x.Name))
        {
            var settings = SettingsOf(c.SettingsJson);
            var isEdge = EdgeUrlLists.LooksLikeEdgeSettings(settings) || Strings(c.AppsJson).Any(IsEdgeApp);
            if (!isEdge)
            {
                continue;
            }

            var edgeRegs = regs.Where(r => IsEdgeApp(r.AppIdentifier) && PlatformMatches(c.Platform, r.DeviceType)).ToList();
            edge.Add(new EdgeConfigView(c, EdgeUrlLists.Analyze(settings, data.Settings.UrlBlocklistLimit, data.Settings.UrlBlocklistReservePercent), edgeRegs.Count, edgeRegs.Count(r => !string.IsNullOrEmpty(r.AppliedPolicies))));
        }

        // ---- Conditional Access that requires a protected app
        var requiring = data.ConditionalAccess.Where(c => c.TargetsMicrosoft365 && (c.RequiresAppProtection || c.RequiresApprovedApp)).ToList();
        var ca = new CaRequirement(
            requiring.Any(c => c.State == "enabled") ? CaRequirementState.Enforced : requiring.Any(c => c.State == "enabledForReportingButNotEnforced") ? CaRequirementState.ReportOnly : CaRequirementState.Absent,
            requiring);

        // ---- people: who is eligible, who is covered
        var mobile = s.Views.Where(v => v.Asset.Platform is "iOS" or "Android" && OperationalStates.IsActive(v.Asset.ActivityLevel)).ToList();
        var eligible = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); // user id -> platforms
        void Eligible(string? id, string platform)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                (eligible.TryGetValue(id, out var set) ? set : eligible[id] = []).Add(platform);
            }
        }

        foreach (var v in mobile) Eligible(v.Asset.IntuneUserId, v.Asset.Platform);
        foreach (var r in regs) Eligible(r.UserId, r.DeviceType ?? "Outro");
        foreach (var a in data.Access.Where(a => a.OperatingSystem is "iOS" or "Android" or "iPadOS")) Eligible(a.UserId, a.OperatingSystem == "iPadOS" ? "iOS" : a.OperatingSystem!);

        var registered = regs.Where(r => !string.IsNullOrWhiteSpace(r.UserId)).GroupBy(r => r.UserId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var lastAccess = data.Access.Where(a => !string.IsNullOrWhiteSpace(a.UserId)).GroupBy(a => a.UserId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Max(a => a.LastAccessAt), StringComparer.OrdinalIgnoreCase);
        int covered = 0, noPolicy = 0, uncovered = 0, withException = 0;
        var gaps = new List<UserGap>();
        foreach (var (id, platforms) in eligible)
        {
            var mine = registered.GetValueOrDefault(id);
            if (mine is not null && mine.Any(r => !string.IsNullOrEmpty(r.AppliedPolicies)))
            {
                covered++;
                continue;
            }

            var ex = ExceptionOf(id);
            if (ex is not null) withException++;
            if (mine is not null)
            {
                noPolicy++;
            }
            else
            {
                uncovered++;
            }

            gaps.Add(new UserGap(id, Upn(id), mine is not null ? "Tem aplicativos registrados, mas nenhuma política de proteção aplicada." : "Tem aparelho móvel ou acesso móvel e nenhum aplicativo protegido por MAM.",
                string.Join(", ", platforms.Order()), lastAccess.TryGetValue(id, out var at) ? at : null, ex));
        }

        var userCoverage = new UserCoverage(eligible.Count, covered, noPolicy, uncovered, withException, gaps.OrderBy(g => g.Exception is not null).ThenByDescending(g => g.LastAccessAt ?? DateTimeOffset.MinValue).ToList());

        // ---- app instances
        var byPlatform = regs.GroupBy(r => r.DeviceType ?? "Outro").ToDictionary(g => g.Key, g => g.Count());
        var top = regs.GroupBy(r => r.AppIdentifier ?? "(sem identificador)").Select(g => (App: g.Key, Count: g.Count(), WithPolicy: g.Count(r => !string.IsNullOrEmpty(r.AppliedPolicies)))).OrderByDescending(x => x.Count).Take(10).ToList();
        var apps = new AppInstances(regs.Count, regs.Count(r => !string.IsNullOrEmpty(r.AppliedPolicies)), regs.Count(r => string.IsNullOrEmpty(r.AppliedPolicies)), byPlatform, top);

        // ---- devices: MDM and MAM are different things
        DeviceModes Modes(string label, IEnumerable<AssetView> list)
        {
            var l = list.ToList();
            return new DeviceModes(label, l.Count(v => v.Asset.IntuneChannel == "Mdm" && v.Asset.HasMam), l.Count(v => v.Asset.IntuneChannel == "Mdm" && !v.Asset.HasMam),
                l.Count(v => v.Asset.IntuneChannel != "Mdm" && v.Asset.HasMam), l.Count(v => v.Asset.IntuneChannel != "Mdm" && !v.Asset.HasMam));
        }

        var activeMobile = s.Views.Where(v => v.Asset.Platform is "iOS" or "Android" && v.Asset.OperationalState != OperationalStates.Decommissioned).ToList();
        var devices = new List<DeviceModes>
        {
            Modes("Celulares e tablets BYOD", activeMobile.Where(v => v.Asset.Ownership == "Personal")),
            Modes("Celulares e tablets corporativos", activeMobile.Where(v => v.Asset.Ownership == "Corporate")),
        };

        // ---- Microsoft 365 access without proof of protection
        var protectedDevice = (AssetView v) => v.Asset.IntuneChannel == "Mdm" || v.Asset.HasMam;
        var withAccess = s.Views.Where(v => v.Asset.LastM365AccessAt is not null && v.Asset.Ownership != "Corporate" || v.Asset.LastM365AccessAt is not null && v.Asset.Platform is "iOS" or "Android").ToList();
        var unprotected = withAccess.Where(v => !protectedDevice(v)).OrderByDescending(v => v.Asset.LastM365AccessAt).ToList();
        var anonymous = data.Access.Where(a => a.EntraDeviceId is null && a.OperatingSystem is "iOS" or "Android" or "iPadOS" && !(registered.ContainsKey(a.UserId ?? ""))).OrderByDescending(a => a.LastAccessAt).ToList();
        var access = new AccessGaps(unprotected.Count, anonymous.Count, unprotected, anonymous);

        // ---- BYOD per platform
        var byod = new List<ByodPlatform>();
        foreach (var platform in new[] { "Android", "iOS" })
        {
            var list = activeMobile.Where(v => v.Asset.Ownership == "Personal" && v.Asset.Platform == platform).ToList();
            byod.Add(new ByodPlatform(platform, list.Count, list.Count(v => v.Asset.IntuneChannel == "Mdm"), list.Count(v => v.Asset.HasMam),
                list.Count(v => v.Asset.IntuneChannel == "Mdm" && v.Asset.HasMam), list.Count(v => v.Asset.IntuneChannel != "Mdm" && !v.Asset.HasMam),
                list.Count(v => v.Asset.IntuneChannel != "Mdm" && !v.Asset.HasMam && v.Asset.LastM365AccessAt is not null)));
        }

        return new MamReport(src.AppPolicies, src.Mam, src.ConditionalAccess, src.SignIns, userCoverage, apps, devices, policyViews, edge, ca, access, exceptions, byod);
    }

    public static bool IsEdgeApp(string? id) => id is not null && (id.Contains("emmx", StringComparison.OrdinalIgnoreCase) || id.Contains("msedge", StringComparison.OrdinalIgnoreCase));

    private static bool PlatformMatches(string configPlatform, string? registrationPlatform) =>
        configPlatform is "Não informada" or "Outro" or "" || string.Equals(configPlatform, registrationPlatform, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> SettingsOf(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } d ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static IReadOnlyList<string> Strings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
