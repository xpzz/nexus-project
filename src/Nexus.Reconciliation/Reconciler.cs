using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

public sealed record ReconcileInput(
    IReadOnlyList<SccmDeviceRecord> Sccm,
    IReadOnlyList<AdComputerRecord> Ad,
    IReadOnlyList<IntuneDeviceRecord> Intune,
    IReadOnlyList<EntraDeviceRecord> Entra,
    IReadOnlyList<AssetLink> PreviousLinks,
    DateTimeOffset Now,
    TimeSpan ActivityWindow);

public sealed record ReviewDraft(string Kind, string Detail, IReadOnlyList<string> Sources);

public sealed record ReconcileResult(IReadOnlyList<Asset> Assets, IReadOnlyList<AssetLink> Links, IReadOnlyList<ReviewDraft> Review);

/// <summary>
/// Joins source records into assets (SPEC §7.2/7.3). Management records are joined only by strong evidence:
/// Entra device id, then a valid serial (with compatible maker/model), then the hardware UUID in conservative cases.
/// A name never joins management records; it only attaches an AD object as low-confidence supporting evidence.
/// Ambiguous cases become review items and are never merged.
/// </summary>
public static class Reconciler
{
    public const string Sccm = "sccm", Intune = "intune", Entra = "entra", Ad = "ad";

    private sealed class Node(string source, string key, object record)
    {
        public string Source { get; } = source;
        public string Key { get; } = key;
        public object Record { get; } = record;
        public string Ref => $"{Source}:{Key}";
    }

    public static ReconcileResult Run(ReconcileInput input)
    {
        var nodes = new List<Node>();
        nodes.AddRange(input.Sccm.Select(r => new Node(Sccm, r.ResourceId.ToString(), r)));
        nodes.AddRange(input.Intune.Select(r => new Node(Intune, r.Id, r)));
        nodes.AddRange(input.Entra.Select(r => new Node(Entra, r.Id, r)));
        nodes.AddRange(input.Ad.Select(r => new Node(Ad, r.ObjectGuid.ToString(), r)));

        var parent = nodes.ToDictionary(n => n.Ref, n => n.Ref);
        var evidenceOf = nodes.ToDictionary(n => n.Ref, _ => ("none", "High", (string?)null));
        var review = new List<ReviewDraft>();

        string Find(string x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(Node a, Node b, string evidence, string confidence, string reason)
        {
            var ra = Find(a.Ref);
            var rb = Find(b.Ref);
            if (ra == rb)
            {
                return;
            }

            parent[rb] = ra;
            // The joined-in record carries the evidence that linked it.
            if (evidenceOf[b.Ref].Item1 == "none")
            {
                evidenceOf[b.Ref] = (evidence, confidence, reason);
            }
            else if (evidenceOf[a.Ref].Item1 == "none")
            {
                evidenceOf[a.Ref] = (evidence, confidence, reason);
            }
        }

        // 1. Entra device id: SCCM AADDeviceID = Intune azureADDeviceId = Entra deviceId.
        foreach (var group in nodes.Where(n => n.Source != Ad && AadId(n) is not null).GroupBy(n => AadId(n)!.Value))
        {
            var members = group.ToList();
            for (var i = 1; i < members.Count; i++)
            {
                Union(members[0], members[i], "aad-device-id", "High", "Mesmo ID de dispositivo do Entra ID.");
            }
        }

        // 2. Valid serial (+ compatible maker). Two distinct SCCM devices with one serial are not merged.
        foreach (var group in nodes.Where(n => n.Source is Sccm or Intune && ValidSerial(SerialOf(n)) is not null)
                     .GroupBy(n => ValidSerial(SerialOf(n))!))
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            var sccmComponents = members.Where(m => m.Source == Sccm).Select(m => Find(m.Ref)).Distinct().Count();
            if (sccmComponents > 1)
            {
                review.Add(new ReviewDraft("DuplicateSerial",
                    $"O número de série {group.Key} aparece em {sccmComponents} dispositivos distintos do SCCM. Nada foi mesclado.",
                    members.Select(m => m.Ref).ToList()));
                continue;
            }

            if (!HardwareCompatible(members))
            {
                review.Add(new ReviewDraft("SerialHardwareMismatch",
                    $"O número de série {group.Key} coincide, mas o fabricante diverge. Nada foi mesclado.",
                    members.Select(m => m.Ref).ToList()));
                continue;
            }

            for (var i = 1; i < members.Count; i++)
            {
                Union(members[0], members[i], "serial", "Medium", "Número de série válido coincide (fabricante compatível).");
            }
        }

        // 3. Hardware UUID, SCCM only and conservative: merge only an obsolete record with exactly one live record.
        foreach (var group in nodes.Where(n => n.Source == Sccm && ValidUuid(((SccmDeviceRecord)n.Record).SmbiosGuid) is not null)
                     .GroupBy(n => ValidUuid(((SccmDeviceRecord)n.Record).SmbiosGuid)!))
        {
            var components = group.GroupBy(n => Find(n.Ref)).Select(g => g.ToList()).ToList();
            if (components.Count < 2)
            {
                continue;
            }

            var live = components.Where(c => c.Any(n => ((SccmDeviceRecord)n.Record).Obsolete != true)).ToList();
            if (live.Count == 1)
            {
                var target = live[0][0];
                foreach (var other in components.Where(c => !ReferenceEquals(c, live[0])))
                {
                    Union(target, other[0], "hardware-uuid", "Medium", "Registro obsoleto com o mesmo UUID de hardware de um único registro ativo (reinstalação).");
                }
            }
            else
            {
                review.Add(new ReviewDraft("CloneSuspect",
                    $"{live.Count} registros ativos do SCCM compartilham o mesmo UUID de hardware (possível clone de máquina virtual). Nada foi mesclado.",
                    group.Select(n => n.Ref).ToList()));
            }
        }

        // 4. AD attaches by name only when exactly one management component and one AD object carry that name.
        var adByName = nodes.Where(n => n.Source == Ad).GroupBy(n => NameKey(((AdComputerRecord)n.Record).Name)).ToDictionary(g => g.Key, g => g.ToList());
        var mgmtByName = nodes.Where(n => n.Source != Ad && NameKey(NameOf(n)) is { Length: > 0 })
            .GroupBy(n => NameKey(NameOf(n)))
            .ToDictionary(g => g.Key, g => g.GroupBy(n => Find(n.Ref)).Select(c => c.ToList()).ToList());
        foreach (var (name, adNodes) in adByName)
        {
            if (name.Length == 0 || !mgmtByName.TryGetValue(name, out var comps))
            {
                continue;
            }

            if (adNodes.Count == 1 && comps.Count == 1)
            {
                Union(comps[0][0], adNodes[0], "name", "Low", "Só o nome coincide (evidência de apoio, não identifica o dispositivo).");
            }
            else
            {
                review.Add(new ReviewDraft("AmbiguousName",
                    $"O nome {name} coincide com {adNodes.Count} objeto(s) do AD e {comps.Count} dispositivo(s) de gerenciamento. O AD não foi associado.",
                    adNodes.Concat(comps.SelectMany(c => c)).Select(n => n.Ref).ToList()));
            }
        }

        return Build(input, nodes, Find, evidenceOf, review);
    }

    private static ReconcileResult Build(ReconcileInput input, List<Node> nodes, Func<string, string> find,
        Dictionary<string, (string, string, string?)> evidenceOf, List<ReviewDraft> review)
    {
        var previous = input.PreviousLinks.GroupBy(l => (l.Source, l.SourceKey)).ToDictionary(g => g.Key, g => g.First().AssetId);
        var components = nodes.GroupBy(n => find(n.Ref)).Select(g => g.OrderBy(n => n.Ref, StringComparer.Ordinal).ToList())
            .OrderBy(c => c[0].Ref, StringComparer.Ordinal).ToList();

        // Review items from multiple Intune records in one asset (re-enrollment / stale registrations).
        foreach (var c in components.Where(c => c.Count(n => n.Source == Intune) > 1))
        {
            review.Add(new ReviewDraft("MultipleIntuneRecords",
                $"{c.Count(n => n.Source == Intune)} registros do Intune para o mesmo equipamento (reenrollment ou registro antigo). Contado como um dispositivo.",
                c.Where(n => n.Source == Intune).Select(n => n.Ref).ToList()));
        }

        var flagged = review.SelectMany(r => r.Sources).ToHashSet();
        var used = new HashSet<Guid>();
        var assets = new List<Asset>();
        var links = new List<AssetLink>();

        foreach (var c in components)
        {
            var id = c.Select(n => previous.TryGetValue((n.Source, n.Key), out var p) ? (Guid?)p : null)
                .Where(p => p is not null).GroupBy(p => p!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => g.Key).FirstOrDefault(g => !used.Contains(g));
            if (id == default)
            {
                id = Guid.NewGuid();
            }

            used.Add(id);
            var asset = BuildAsset(id, c, input, flagged, evidenceOf);
            assets.Add(asset);
            links.AddRange(c.Select(n =>
            {
                var (evidence, confidence, reason) = evidenceOf[n.Ref];
                return new AssetLink { AssetId = id, Source = n.Source, SourceKey = n.Key, Evidence = evidence, Confidence = confidence, Reason = reason };
            }));
        }

        return new ReconcileResult(assets, links, review);
    }

    private static Asset BuildAsset(Guid id, List<Node> c, ReconcileInput input, HashSet<string> flagged, Dictionary<string, (string, string, string?)> evidenceOf)
    {
        var sccm = c.Where(n => n.Source == Sccm).Select(n => (SccmDeviceRecord)n.Record)
            .OrderBy(r => r.Obsolete == true).ThenByDescending(r => r.LastActiveAt ?? DateTimeOffset.MinValue).ToList();
        var intune = c.Where(n => n.Source == Intune).Select(n => (IntuneDeviceRecord)n.Record)
            .OrderByDescending(r => r.LastSyncAt ?? DateTimeOffset.MinValue).ToList();
        var entra = c.Where(n => n.Source == Entra).Select(n => (EntraDeviceRecord)n.Record)
            .OrderByDescending(r => r.LastSignInAt ?? DateTimeOffset.MinValue).ToList();
        var ad = c.Where(n => n.Source == Ad).Select(n => (AdComputerRecord)n.Record).ToList();

        var primarySccm = sccm.FirstOrDefault();
        var primaryIntune = intune.FirstOrDefault();
        var primaryEntra = entra.FirstOrDefault();
        var primaryAd = ad.FirstOrDefault();

        var asset = new Asset
        {
            Id = id,
            Name = primarySccm?.Name ?? primaryIntune?.DeviceName ?? primaryEntra?.DisplayName ?? primaryAd?.Name ?? "(sem nome)",
            Serial = sccm.Select(r => ValidSerial(r.Serial)).Concat(intune.Select(r => ValidSerial(r.SerialNumber))).FirstOrDefault(s => s is not null),
            Manufacturer = Pick(sccm.Select(r => r.Manufacturer).Concat(intune.Select(r => r.Manufacturer))),
            Model = Pick(sccm.Select(r => r.Model).Concat(intune.Select(r => r.Model))),
            PrimaryUser = primaryIntune?.UserPrincipalName,
            UpdatedAt = input.Now,
            InSccm = sccm.Count > 0,
            InIntune = intune.Count > 0,
            InEntra = entra.Count > 0,
            InAd = ad.Count > 0,
            AdEnabled = primaryAd?.Enabled ?? true,
            AdByNameOnly = c.Any(n => n.Source == Ad && evidenceOf[n.Ref].Item1 == "name"),
            ComplianceState = primaryIntune?.ComplianceState,
            EntraTrustType = primaryEntra?.TrustType,
        };

        asset.OperatingSystem = primarySccm?.OperatingSystem ?? primaryIntune?.OperatingSystem ?? primaryEntra?.OperatingSystem ?? primaryAd?.OperatingSystem;
        asset.OsVersion = primaryIntune?.OsVersion ?? primaryEntra?.OperatingSystemVersion ?? primaryAd?.OperatingSystemVersion;
        asset.Platform = PlatformOf(primaryIntune?.OperatingSystem ?? primarySccm?.OperatingSystem ?? primaryEntra?.OperatingSystem ?? primaryAd?.OperatingSystem);

        (asset.Ownership, asset.OwnershipSource) = OwnershipOf(primaryIntune, primaryEntra, asset);

        asset.SccmClient = primarySccm is { Client: true, Obsolete: not true };
        asset.SccmHealth = primarySccm switch
        {
            null => "NotApplicable",
            { Obsolete: true } => "Obsolete",
            { Client: not true } => "NoClient",
            { Active: not true } or { ClientActiveStatus: 0 } => "Inactive",
            _ => "Healthy",
        };
        asset.SccmLastSeenAt = primarySccm?.LastActiveAt;
        asset.IntuneChannel = intune.Count == 0 ? "None" : intune.Select(r => ChannelOf(r.ManagementAgent)).MinBy(ChannelRank)!;
        asset.IntuneLastSyncAt = primaryIntune?.LastSyncAt;
        asset.EntraLastSignInAt = primaryEntra?.LastSignInAt;
        asset.AdLastLogonAt = primaryAd?.LastLogonTimestamp;

        asset.LastActivityAt = new[] { asset.SccmLastSeenAt, asset.IntuneLastSyncAt, asset.EntraLastSignInAt, asset.AdLastLogonAt }.Max();
        asset.IsActive = asset.LastActivityAt is { } last && input.Now - last <= input.ActivityWindow;

        var hasClient = asset.SccmClient;
        var hasMdm = asset.IntuneChannel == "Mdm";
        asset.Coverage = (hasClient, hasMdm) switch { (true, true) => "Both", (true, false) => "OnlySccm", (false, true) => "OnlyIntune", _ => "Neither" };

        var weakest = c.Where(n => n.Source != Ad).Select(n => evidenceOf[n.Ref].Item2 switch { "Low" => 2, "Medium" => 1, _ => 0 }).DefaultIfEmpty(0).Max();
        asset.Confidence = weakest switch { 2 => "Low", 1 => "Medium", _ => "High" };
        asset.NeedsReview = c.Any(n => flagged.Contains(n.Ref));
        return asset;
    }

    private static string? Pick(IEnumerable<string?> values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static (string, string?) OwnershipOf(IntuneDeviceRecord? intune, EntraDeviceRecord? entra, Asset asset)
    {
        if (intune?.OwnerType is { } owner)
        {
            if (owner.Equals("company", StringComparison.OrdinalIgnoreCase))
            {
                return ("Corporate", "intune");
            }

            if (owner.Equals("personal", StringComparison.OrdinalIgnoreCase))
            {
                return ("Personal", "intune");
            }
        }

        if (entra?.Ownership is { } e)
        {
            if (e.Equals("Company", StringComparison.OrdinalIgnoreCase))
            {
                return ("Corporate", "entra");
            }

            if (e.Equals("Personal", StringComparison.OrdinalIgnoreCase))
            {
                return ("Personal", "entra");
            }
        }

        return asset.InSccm || asset.InAd ? ("Corporate", "domain") : ("Unknown", null);
    }

    public static string PlatformOf(string? os)
    {
        if (string.IsNullOrWhiteSpace(os))
        {
            return "Other";
        }

        if (os.Contains("Server", StringComparison.OrdinalIgnoreCase) && os.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "WindowsServer";
        }

        if (os.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "WindowsClient";
        }

        if (os.Contains("iOS", StringComparison.OrdinalIgnoreCase) || os.Contains("iPadOS", StringComparison.OrdinalIgnoreCase))
        {
            return "iOS";
        }

        if (os.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Android";
        }

        return os.Contains("mac", StringComparison.OrdinalIgnoreCase) ? "macOS" : "Other";
    }

    /// <summary>Mdm only when the agent proves MDM enrollment; tenant attach (configurationManagerClient) and Defender (msSense) are not MDM.</summary>
    public static string ChannelOf(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return "Other";
        }

        if (agent.Equals("msSense", StringComparison.OrdinalIgnoreCase))
        {
            return "SecurityManagement";
        }

        if (agent.Equals("configurationManagerClient", StringComparison.OrdinalIgnoreCase))
        {
            return "TenantAttach";
        }

        return agent.Contains("mdm", StringComparison.OrdinalIgnoreCase) || agent.Contains("intuneClient", StringComparison.OrdinalIgnoreCase)
            ? "Mdm"
            : "Other";
    }

    private static int ChannelRank(string channel) => channel switch { "Mdm" => 0, "TenantAttach" => 1, "SecurityManagement" => 2, _ => 3 };

    private static Guid? AadId(Node n)
    {
        var id = n.Record switch
        {
            SccmDeviceRecord s => s.AadDeviceId,
            IntuneDeviceRecord i => i.AzureAdDeviceId,
            EntraDeviceRecord e => e.DeviceId,
            _ => null,
        };
        return id is { } g && g != Guid.Empty ? g : null; // an empty GUID is never an identifier
    }

    private static string? SerialOf(Node n) => n.Record switch
    {
        SccmDeviceRecord s => s.Serial,
        IntuneDeviceRecord i => i.SerialNumber,
        _ => null,
    };

    private static string NameOf(Node n) => n.Record switch
    {
        SccmDeviceRecord s => s.Name ?? "",
        IntuneDeviceRecord i => i.DeviceName ?? "",
        EntraDeviceRecord e => e.DisplayName ?? "",
        _ => "",
    };

    private static string NameKey(string name) => name.Trim().ToLowerInvariant();

    private static readonly string[] InvalidSerialFragments =
        ["to be filled", "default string", "system serial", "serial number", "not specified", "not applicable", "o.e.m", "none", "unknown", "n/a", "chassis"];

    public static string? ValidSerial(string? serial)
    {
        var s = serial?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length < 4)
        {
            return null;
        }

        if (s.Distinct().Count() == 1 || s.All(ch => ch == '0' || ch == '-'))
        {
            return null;
        }

        var lower = s.ToLowerInvariant();
        if (InvalidSerialFragments.Any(f => lower.Equals(f) || lower.Contains(f)))
        {
            return null;
        }

        return s.ToUpperInvariant();
    }

    private static string? ValidUuid(string? uuid)
    {
        var u = uuid?.Trim();
        if (string.IsNullOrEmpty(u))
        {
            return null;
        }

        var hex = new string(u.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return hex.Length < 8 || hex.Distinct().Count() == 1 || hex.All(ch => ch is '0' or 'F') ? null : hex;
    }

    private static bool HardwareCompatible(List<Node> members)
    {
        static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToLowerInvariant();
        // Only the manufacturer is compared: model strings differ between SCCM (machine type) and Intune (marketing name).
        var makers = members.Select(m => Norm(m.Record is SccmDeviceRecord s ? s.Manufacturer : ((IntuneDeviceRecord)m.Record).Manufacturer))
            .Where(v => v is not null).Select(v => v!.Split(' ', ',', '.')[0]).Distinct().ToArray();
        return makers.Length <= 1;
    }
}
