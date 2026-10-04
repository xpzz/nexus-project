using Nexus.Collectors.Graph;
using Nexus.Collectors.Sccm;
using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

public sealed record ReconcileInput(
    IReadOnlyList<SccmDeviceRecord> Sccm,
    IReadOnlyList<AdComputerRecord> Ad,
    IReadOnlyList<IntuneDeviceRecord> Intune,
    IReadOnlyList<EntraDeviceRecord> Entra,
    IReadOnlyList<AssetLink> PreviousLinks,
    DateTimeOffset Now,
    TimeSpan ActivityWindow,
    IReadOnlyList<EntraUserRecord>? Users = null,
    IReadOnlyList<IntuneDevicePolicyState>? PolicyStates = null,
    IReadOnlyList<MamRegistrationRecord>? Mam = null,
    bool PoliciesCollected = false,
    IReadOnlyList<XdrEndpointRecord>? Xdr = null,
    IReadOnlyList<NetskopeClientRecord>? Netskope = null,
    EvidencePolicy? Evidence = null,
    IReadOnlyList<AccessEvidenceRecord>? Access = null);

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
    public const string Sccm = "sccm", Intune = "intune", Entra = "entra", Ad = "ad", Xdr = "xdr", Netskope = "netskope";

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
        nodes.AddRange((input.Xdr ?? []).Select(r => new Node(Xdr, r.AgentId, r)));
        nodes.AddRange((input.Netskope ?? []).Select(r => new Node(Netskope, r.Id, r)));

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
        foreach (var group in nodes.Where(n => n.Source is Sccm or Intune or Entra && AadId(n) is not null).GroupBy(n => AadId(n)!.Value))
        {
            var members = group.ToList();
            for (var i = 1; i < members.Count; i++)
            {
                Union(members[0], members[i], "aad-device-id", "High", "Mesmo ID de dispositivo do Entra ID.");
            }
        }

        // 1b. Netskope reports the identifier the management tool gave the device. When it is an Entra device id or an Intune device id, it is a strong key.
        var byAad = nodes.Where(n => n.Source is Sccm or Intune or Entra && AadId(n) is not null).GroupBy(n => AadId(n)!.Value).ToDictionary(g => g.Key, g => g.First());
        var byIntuneId = nodes.Where(n => n.Source == Intune && Guid.TryParse(((IntuneDeviceRecord)n.Record).Id, out _))
            .GroupBy(n => Guid.Parse(((IntuneDeviceRecord)n.Record).Id)).ToDictionary(g => g.Key, g => g.First());
        foreach (var net in nodes.Where(n => n.Source == Netskope))
        {
            if (Guid.TryParse(((NetskopeClientRecord)net.Record).ManagementId, out var managementGuid) && managementGuid != Guid.Empty
                && (byAad.GetValueOrDefault(managementGuid) ?? byIntuneId.GetValueOrDefault(managementGuid)) is { } target)
            {
                Union(target, net, "management-id", "High", "O ID de gerenciamento do cliente Netskope é o ID do dispositivo no Entra ID ou no Intune.");
            }
        }

        // 2. Valid serial (+ compatible maker). Two distinct SCCM devices with one serial are not merged.
        foreach (var group in nodes.Where(n => n.Source is Sccm or Intune or Netskope && ValidSerial(SerialOf(n)) is not null)
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

        // 4. Supporting sources (AD, XDR) attach by name only: to exactly one management component, and only when the name is not ambiguous.
        //    Several agents of one host name (a reinstall) all attach to that single asset and raise an informational review.
        var mgmtByName = nodes.Where(n => n.Source is Sccm or Intune or Entra && NameKey(NameOf(n)) is { Length: > 0 })
            .GroupBy(n => NameKey(NameOf(n)))
            .ToDictionary(g => g.Key, g => g.GroupBy(n => Find(n.Ref)).Select(c => c.ToList()).ToList());
        var supportByName = nodes.Where(n => n.Source is Ad or Xdr or Netskope && NameKey(NameOf(n)) is { Length: > 0 }).GroupBy(n => NameKey(NameOf(n)));
        const string nameReason = "Só o nome coincide (evidência de apoio, não identifica o dispositivo).";
        foreach (var group in supportByName)
        {
            var ads = group.Where(n => n.Source == Ad).ToList();
            var xdrs = group.Where(n => n.Source is Xdr or Netskope).ToList(); // endpoint agents (XDR, Netskope)
            if (mgmtByName.TryGetValue(group.Key, out var comps))
            {
                if (comps.Count == 1)
                {
                    if (ads.Count == 1)
                    {
                        Union(comps[0][0], ads[0], "name", "Low", nameReason);
                    }
                    else if (ads.Count > 1)
                    {
                        review.Add(new ReviewDraft("AmbiguousName", $"O nome {group.Key} coincide com {ads.Count} objetos do AD e 1 dispositivo de gerenciamento. O AD não foi associado.",
                            ads.Concat(comps[0]).Select(n => n.Ref).ToList()));
                    }

                    foreach (var x in xdrs)
                    {
                        Union(comps[0][0], x, "name", "Low", nameReason);
                    }
                }
                else
                {
                    review.Add(new ReviewDraft("AmbiguousName",
                        $"O nome {group.Key} coincide com {ads.Count} objeto(s) do AD, {xdrs.Count} agente(s) (XDR ou Netskope) e {comps.Count} dispositivo(s) de gerenciamento. Nada foi associado.",
                        ads.Concat(xdrs).Concat(comps.SelectMany(c => c)).Select(n => n.Ref).ToList()));
                }

                continue;
            }

            // No management record with this name: the AD object and the XDR agents describe the same host.
            if (ads.Count == 1)
            {
                foreach (var x in xdrs)
                {
                    Union(ads[0], x, "name", "Low", nameReason);
                }
            }
            else if (ads.Count > 1 && xdrs.Count > 0)
            {
                review.Add(new ReviewDraft("AmbiguousName", $"O nome {group.Key} coincide com {ads.Count} objetos do AD e {xdrs.Count} agente(s) (XDR ou Netskope). Nada foi associado.", ads.Concat(xdrs).Select(n => n.Ref).ToList()));
            }
            else if (ads.Count == 0)
            {
                for (var i = 1; i < xdrs.Count; i++)
                {
                    Union(xdrs[0], xdrs[i], "name", "Low", nameReason);
                }
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

        foreach (var c in components.Where(c => c.Count(n => n.Source == Xdr) > 1))
        {
            review.Add(new ReviewDraft("MultipleXdrRecords",
                $"{c.Count(n => n.Source == Xdr)} agentes do XDR com o mesmo nome (reinstalação ou agente antigo). Contado como um dispositivo.",
                c.Where(n => n.Source == Xdr).Select(n => n.Ref).ToList()));
        }

        foreach (var c in components.Where(c => c.Count(n => n.Source == Netskope) > 1))
        {
            review.Add(new ReviewDraft("MultipleNetskopeRecords",
                $"{c.Count(n => n.Source == Netskope)} clientes Netskope para o mesmo equipamento (reinstalação ou cliente antigo). Contado como um dispositivo.",
                c.Where(n => n.Source == Netskope).Select(n => n.Ref).ToList()));
        }

        var flagged = review.SelectMany(r => r.Sources).ToHashSet();
        var used = new HashSet<Guid>();
        var assets = new List<Asset>();
        var links = new List<AssetLink>();
        var lookups = Lookups.From(input);

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
            var asset = BuildAsset(id, c, input, flagged, evidenceOf, lookups);
            assets.Add(asset);
            links.AddRange(c.Select(n =>
            {
                var (evidence, confidence, reason) = evidenceOf[n.Ref];
                return new AssetLink { AssetId = id, Source = n.Source, SourceKey = n.Key, Evidence = evidence, Confidence = confidence, Reason = reason };
            }));
        }

        AttachMam(input, assets, links, previous, used, review);
        ReviewSharedMacs(assets, links, review);

        // Last step, once every source is attached: the evidence engine decides the operational state with the type's own thresholds.
        var policy = input.Evidence ?? EvidencePolicy.FromWindow(input.ActivityWindow);
        foreach (var asset in assets)
        {
            EvidenceEngine.Apply(asset, input.Now, policy);
        }

        return new ReconcileResult(assets, links, review);
    }

    private static Asset BuildAsset(Guid id, List<Node> c, ReconcileInput input, HashSet<string> flagged, Dictionary<string, (string, string, string?)> evidenceOf, Lookups lookups)
    {
        var sccm = c.Where(n => n.Source == Sccm).Select(n => (SccmDeviceRecord)n.Record)
            .OrderBy(r => r.Obsolete == true).ThenByDescending(r => r.LastActiveAt ?? DateTimeOffset.MinValue).ToList();
        var intune = c.Where(n => n.Source == Intune).Select(n => (IntuneDeviceRecord)n.Record)
            .OrderByDescending(r => r.LastSyncAt ?? DateTimeOffset.MinValue).ToList();
        var entra = c.Where(n => n.Source == Entra).Select(n => (EntraDeviceRecord)n.Record)
            .OrderByDescending(r => r.LastSignInAt ?? DateTimeOffset.MinValue).ToList();
        var ad = c.Where(n => n.Source == Ad).Select(n => (AdComputerRecord)n.Record).ToList();
        var xdr = c.Where(n => n.Source == Xdr).Select(n => (XdrEndpointRecord)n.Record)
            .OrderByDescending(r => IsXdrConnected(r.AgentStatus)).ThenByDescending(r => r.LastSeenAt ?? DateTimeOffset.MinValue).ToList();

        var netskope = c.Where(n => n.Source == Netskope).Select(n => (NetskopeClientRecord)n.Record)
            .OrderByDescending(r => r.LastEventAt ?? DateTimeOffset.MinValue).ToList();

        var primarySccm = sccm.FirstOrDefault();
        var primaryIntune = intune.FirstOrDefault();
        var primaryEntra = entra.FirstOrDefault();
        var primaryAd = ad.FirstOrDefault();
        var primaryXdr = xdr.FirstOrDefault();
        var primaryNetskope = netskope.FirstOrDefault();

        var asset = new Asset
        {
            Id = id,
            Name = primarySccm?.Name ?? primaryIntune?.DeviceName ?? primaryEntra?.DisplayName ?? primaryAd?.Name ?? ShortName(primaryXdr?.HostName) ?? ShortName(primaryNetskope?.HostName) ?? "(sem nome)",
            Serial = sccm.Select(r => ValidSerial(r.Serial)).Concat(intune.Select(r => ValidSerial(r.SerialNumber))).Concat(netskope.Select(r => ValidSerial(r.Serial))).FirstOrDefault(s => s is not null),
            Manufacturer = Pick(sccm.Select(r => r.Manufacturer).Concat(intune.Select(r => r.Manufacturer)).Concat(netskope.Select(r => r.Manufacturer))),
            Model = Pick(sccm.Select(r => r.Model).Concat(intune.Select(r => r.Model)).Concat(netskope.Select(r => r.Model))),
            PrimaryUser = primaryIntune?.UserPrincipalName,
            UpdatedAt = input.Now,
            InSccm = sccm.Count > 0,
            InIntune = intune.Count > 0,
            InEntra = entra.Count > 0,
            InAd = ad.Count > 0,
            InNetskope = netskope.Count > 0,
            NetskopeByNameOnly = c.Any(n => n.Source == Netskope && evidenceOf[n.Ref].Item1 == "name"),
            NetskopeStatus = primaryNetskope?.Status,
            NetskopeVersion = primaryNetskope?.ClientVersion,
            NetskopeLastSeenAt = primaryNetskope?.LastEventAt,
            InXdr = xdr.Count > 0,
            XdrByNameOnly = c.Any(n => n.Source == Xdr && evidenceOf[n.Ref].Item1 == "name"),
            XdrStatus = primaryXdr?.AgentStatus,
            XdrOperationalStatus = primaryXdr?.OperationalStatus,
            XdrAgentType = primaryXdr?.AgentType,
            XdrIp = primaryXdr?.Ip,
            XdrLastSeenAt = primaryXdr?.LastSeenAt,
            AdEnabled = primaryAd?.Enabled ?? true,
            AdByNameOnly = c.Any(n => n.Source == Ad && evidenceOf[n.Ref].Item1 == "name"),
            ComplianceState = primaryIntune?.ComplianceState,
            EntraTrustType = primaryEntra?.TrustType,
        };

        asset.OperatingSystem = primarySccm?.OperatingSystem ?? primaryIntune?.OperatingSystem ?? primaryEntra?.OperatingSystem ?? primaryAd?.OperatingSystem ?? primaryNetskope?.OperatingSystem;
        asset.OsVersion = primaryIntune?.OsVersion ?? primaryEntra?.OperatingSystemVersion ?? primaryAd?.OperatingSystemVersion ?? primaryNetskope?.OsVersion;
        asset.Platform = PlatformOf(primaryIntune?.OperatingSystem ?? primarySccm?.OperatingSystem ?? primaryEntra?.OperatingSystem ?? primaryAd?.OperatingSystem ?? primaryNetskope?.OperatingSystem);

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
        // Any contact that comes from the SCCM client counts as the client being alive: last active time, heartbeat (DDR), policy request, hardware scan.
        asset.SccmLastSeenAt = primarySccm is null ? null : new[] { primarySccm.LastActiveAt, primarySccm.LastDdrAt, primarySccm.LastPolicyRequestAt, primarySccm.LastHwScanAt }.Max();
        asset.SccmLastDdrAt = primarySccm?.LastDdrAt;
        asset.SccmLastSwScanAt = primarySccm?.LastSwScanAt;
        asset.Uuid = primarySccm?.SmbiosGuid;
        asset.Fqdn = primaryAd?.DnsHostName ?? (primarySccm is { Domain.Length: > 0, Name.Length: > 0 } ? $"{primarySccm.Name}.{primarySccm.Domain}".ToLowerInvariant() : null);
        asset.IpAddresses = string.Join(",", sccm.SelectMany(r => (r.IpAddresses ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)).Concat([primaryXdr?.Ip ?? ""]).Where(x => x.Length > 0).Distinct().Take(8)) is { Length: > 0 } ips ? ips : null;
        asset.MacAddresses = string.Join(",", sccm.SelectMany(r => (r.MacAddresses ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)).Concat(intune.SelectMany(r => new[] { r.EthernetMac, r.WifiMac }.Where(m => m is not null).Select(m => m!))).Distinct()) is { Length: > 0 } macs ? macs : null;
        asset.Chassis = primarySccm?.Chassis;
        asset.LastUser = primarySccm?.LastLogonUser ?? primaryIntune?.UserPrincipalName;
        asset.IntuneEnrollmentType = primaryIntune?.EnrollmentType;
        asset.IntuneOwnerType = primaryIntune?.OwnerType;
        asset.IntuneRegistrationState = primaryIntune?.DeviceRegistrationState;
        asset.IntuneSupervised = primaryIntune?.IsSupervised;
        asset.IntuneChannel = intune.Count == 0 ? "None" : intune.Select(r => ChannelOf(r.ManagementAgent)).MinBy(ChannelRank)!;
        asset.IntuneLastSyncAt = primaryIntune?.LastSyncAt;
        asset.EntraLastSignInAt = primaryEntra?.LastSignInAt;
        asset.AdLastLogonAt = primaryAd?.LastLogonTimestamp;

        // Sign-ins that carry the Entra device id prove the device was used to reach Microsoft 365. The id is the one Entra and Intune share.
        var access = entra.Select(e => e.DeviceId).Concat(intune.Select(i => i.AzureAdDeviceId)).Where(g => g is not null)
            .Select(g => lookups.AccessByDevice.GetValueOrDefault(g!.Value.ToString().ToLowerInvariant())).Where(a => a is not null).MaxBy(a => a!.LastAccessAt);
        asset.LastM365AccessAt = access?.LastAccessAt;
        asset.M365Workloads = access?.Workloads;

        ActivityModel.Apply(asset, input.Now, input.ActivityWindow);

        var hasClient = asset.SccmClient;
        var hasMdm = asset.IntuneChannel == "Mdm";
        asset.Coverage = (hasClient, hasMdm) switch { (true, true) => "Both", (true, false) => "OnlySccm", (false, true) => "OnlyIntune", _ => "Neither" };

        ApplyDetails(asset, primarySccm, primaryIntune, ad.FirstOrDefault(), lookups);

        var weakest = c.Where(n => n.Source is Sccm or Intune or Entra || (n.Source == Netskope && evidenceOf[n.Ref].Item1 != "name")).Select(n => evidenceOf[n.Ref].Item2 switch { "Low" => 2, "Medium" => 1, _ => 0 }).DefaultIfEmpty(0).Max();
        asset.Confidence = weakest switch { 2 => "Low", 1 => "Medium", _ => "High" };
        asset.NeedsReview = c.Any(n => flagged.Contains(n.Ref));
        return asset;
    }

    /// <summary>Lookups built once per run: users and policy counts per Intune device.</summary>
    private sealed class Lookups
    {
        public Dictionary<string, AccessEvidenceRecord> AccessByDevice { get; init; } = [];
        public Dictionary<string, EntraUserRecord> UsersById { get; init; } = [];
        public Dictionary<string, EntraUserRecord> UsersByUpn { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (int Compliance, int ComplianceFailed, int Config, int ConfigFailed)> Policies { get; init; } = [];
        public bool PoliciesCollected { get; init; }

        public static Lookups From(ReconcileInput input)
        {
            var users = input.Users ?? [];
            var policies = new Dictionary<string, (int, int, int, int)>();
            foreach (var g in (input.PolicyStates ?? []).GroupBy(p => p.IntuneDeviceId))
            {
                int Applied(string kind) => g.Count(p => p.Kind == kind && !IsNotApplied(p.State));
                int Failed(string kind) => g.Count(p => p.Kind == kind && IsFailed(p.State));
                policies[g.Key] = (Applied(PolicyKindsForReconcile.Compliance), Failed(PolicyKindsForReconcile.Compliance), Applied(PolicyKindsForReconcile.Configuration), Failed(PolicyKindsForReconcile.Configuration));
            }

            return new Lookups
            {
                AccessByDevice = (input.Access ?? []).Where(a => GraphIds.IsUsable(a.EntraDeviceId)).GroupBy(a => a.EntraDeviceId!.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.LastAccessAt).First()),
                UsersById = users.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First()),
                UsersByUpn = users.Where(u => !string.IsNullOrEmpty(u.UserPrincipalName)).GroupBy(u => u.UserPrincipalName!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                Policies = policies,
                PoliciesCollected = input.PoliciesCollected,
            };
        }
    }

    private static class PolicyKindsForReconcile
    {
        public const string Compliance = "compliance", Configuration = "configuration";
    }

    public static bool IsNotApplied(string state) => state is "notApplicable" or "notAssigned" or "unknown";

    public static bool IsFailed(string state) => state is "nonCompliant" or "error" or "conflict" or "failed";

    private static void ApplyDetails(Asset asset, SccmDeviceRecord? sccm, IntuneDeviceRecord? intune, AdComputerRecord? ad, Lookups lookups)
    {
        if (sccm is not null)
        {
            asset.SccmLastPolicyAt = sccm.LastPolicyRequestAt;
            asset.SccmLastHwScanAt = sccm.LastHwScanAt;
            asset.SccmClientVersion = sccm.ClientVersion;
            asset.CpuName = sccm.CpuName;
            asset.MemoryMb = sccm.MemoryMb;
            asset.DiskTotalMb = sccm.DiskTotalMb;
            asset.DiskFreeMb = sccm.DiskFreeMb;
            asset.OsVersion = sccm.OsVersion ?? asset.OsVersion;
        }

        if (intune is not null)
        {
            asset.IntuneUserId = intune.UserId;
            asset.IsEncrypted = intune.IsEncrypted;
            asset.JailBroken = intune.JailBroken switch { "True" => true, "False" => false, _ => null };
            asset.MemoryMb ??= intune.PhysicalMemoryBytes is { } mem ? mem / (1024 * 1024) : null;
            asset.DiskTotalMb ??= intune.TotalStorageBytes is { } total ? total / (1024 * 1024) : null;
            asset.DiskFreeMb ??= intune.FreeStorageBytes is { } free ? free / (1024 * 1024) : null;

            var user = intune.UserId is { } uid && lookups.UsersById.TryGetValue(uid, out var byId) ? byId
                : intune.UserPrincipalName is { } upn && lookups.UsersByUpn.TryGetValue(upn, out var byUpn) ? byUpn : null;
            if (user is not null)
            {
                asset.Department = user.Department;
                asset.UserEnabled = user.AccountEnabled;
                asset.PrimaryUser ??= user.UserPrincipalName;
            }

            if (lookups.PoliciesCollected)
            {
                asset.PoliciesCollected = true;
                var (compliance, complianceFailed, config, configFailed) = lookups.Policies.GetValueOrDefault(intune.Id);
                asset.CompliancePolicies = compliance;
                asset.CompliancePoliciesFailed = complianceFailed;
                asset.ConfigProfiles = config;
                asset.ConfigProfilesFailed = configFailed;
            }
        }
        else if (sccm?.LastLogonUser is { Length: > 0 } logon)
        {
            asset.PrimaryUser ??= logon;
        }
    }

    private static string FamilyOf(string platform) => platform switch { "WindowsClient" or "WindowsServer" => "Windows", _ => platform };

    /// <summary>
    /// App protection (MAM) registrations belong to a user and a device tag. They join an asset only when the user has exactly one
    /// Intune device of that platform; otherwise each device tag becomes its own asset (a personal device protected only by MAM),
    /// and an ambiguous user goes to the review queue.
    /// </summary>
    /// <summary>
    /// A hardware MAC address that appears on two different assets is worth a look (a cloned image, a swapped network card, a record that should have been merged).
    /// Virtual, locally administered and zero addresses, and addresses seen on more than four assets (docks, NAT devices), are ignored. Informational: nothing is merged.
    /// </summary>
    private static void ReviewSharedMacs(List<Asset> assets, List<AssetLink> links, List<ReviewDraft> review)
    {
        var linkByAsset = links.GroupBy(l => l.AssetId).ToDictionary(g => g.Key, g => g.First());
        var shared = assets.SelectMany(a => (a.MacAddresses ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(NetworkIds.IsIdentifying).Distinct().Select(m => (Mac: m, Asset: a)))
            .GroupBy(x => x.Mac).Where(g => g.Select(x => x.Asset.Id).Distinct().Count() is > 1 and <= 4).OrderBy(g => g.Key, StringComparer.Ordinal).Take(200);
        foreach (var g in shared)
        {
            var names = g.Select(x => x.Asset).DistinctBy(a => a.Id).ToList();
            review.Add(new ReviewDraft("SharedMac", $"O endereço MAC {g.Key} aparece em {names.Count} equipamentos diferentes ({string.Join(", ", names.Select(a => a.Name))}). Pode ser imagem clonada, placa trocada ou registro duplicado. Nada foi mesclado.",
                names.Where(a => linkByAsset.ContainsKey(a.Id)).Select(a => $"{linkByAsset[a.Id].Source}:{linkByAsset[a.Id].SourceKey}").ToList()));
        }
    }

    private static void AttachMam(ReconcileInput input, List<Asset> assets, List<AssetLink> links, Dictionary<(string, string), Guid> previous, HashSet<Guid> used, List<ReviewDraft> review)
    {
        var registrations = input.Mam ?? [];
        if (registrations.Count == 0)
        {
            return;
        }

        var byUserPlatform = assets.Where(a => a.IntuneUserId is not null && a.InIntune).GroupBy(a => (a.IntuneUserId!, FamilyOf(a.Platform))).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in registrations.Where(r => GraphIds.IsUsable(r.UserId)).GroupBy(r => (r.UserId!, Platform: r.DeviceType ?? "Other")))
        {
            var candidates = byUserPlatform.GetValueOrDefault((group.Key.Item1, group.Key.Platform)) ?? [];
            if (candidates.Count == 1)
            {
                var asset = candidates[0];
                Apply(asset, group.ToList());
                links.Add(new AssetLink
                {
                    AssetId = asset.Id, Source = "mam", SourceKey = $"{group.Key.Item1}|{group.Key.Platform}", Evidence = "user-platform", Confidence = "Medium",
                    Reason = "O usuário tem um único dispositivo desta plataforma no Intune.",
                });
                continue;
            }

            foreach (var device in group.GroupBy(r => r.DeviceTag ?? r.DeviceName ?? r.Id))
            {
                var key = "tag:" + device.Key;
                var id = previous.TryGetValue(("mam", key), out var p) && !used.Contains(p) ? p : Guid.NewGuid();
                used.Add(id);
                var first = device.First();
                var mamOnly = new Asset
                {
                    Id = id, Name = first.DeviceName ?? device.Key, Platform = group.Key.Platform == "Windows" ? "WindowsClient" : group.Key.Platform, Ownership = "Personal", OwnershipSource = "mam",
                    IntuneChannel = "None", Coverage = "OnlyMam", Confidence = candidates.Count > 1 ? "Low" : "High", UpdatedAt = input.Now,
                    PrimaryUser = input.Users?.FirstOrDefault(u => u.Id == group.Key.Item1)?.UserPrincipalName ?? group.Key.Item1,
                    Department = input.Users?.FirstOrDefault(u => u.Id == group.Key.Item1)?.Department,
                    UserEnabled = input.Users?.FirstOrDefault(u => u.Id == group.Key.Item1)?.AccountEnabled,
                    IntuneUserId = group.Key.Item1, OperatingSystem = group.Key.Platform, OsVersion = first.PlatformVersion,
                };
                Apply(mamOnly, device.ToList());
                if (candidates.Count > 1)
                {
                    mamOnly.NeedsReview = true;
                    review.Add(new ReviewDraft("MamAmbiguous",
                        $"O usuário tem {candidates.Count} dispositivos {group.Key.Platform} no Intune; os registros de proteção de apps do aparelho {mamOnly.Name} não foram associados a nenhum deles.",
                        [$"mam:{key}"]));
                }

                assets.Add(mamOnly);
                links.Add(new AssetLink { AssetId = id, Source = "mam", SourceKey = key, Evidence = "mam-registration", Confidence = mamOnly.Confidence, Reason = "Aparelho conhecido apenas pela proteção de aplicativos." });
            }
        }

        // Registrations Graph returns without a usable user (null, blank or zero GUID) cannot be tied to anyone: each device still becomes a MAM-only
        // asset, flagged by the missing user (data-quality page) instead of being dropped or failing the run.
        var orphans = registrations.Where(r => !GraphIds.IsUsable(r.UserId)).GroupBy(r => (r.DeviceTag ?? r.DeviceName ?? r.Id, Platform: r.DeviceType ?? "Other")).ToList();
        foreach (var device in orphans)
        {
            var key = "tag:" + device.Key.Item1;
            var id = previous.TryGetValue(("mam", key), out var p) && !used.Contains(p) ? p : Guid.NewGuid();
            used.Add(id);
            var first = device.First();
            var mamOnly = new Asset
            {
                Id = id, Name = first.DeviceName ?? device.Key.Item1, Platform = device.Key.Platform == "Windows" ? "WindowsClient" : device.Key.Platform, Ownership = "Personal", OwnershipSource = "mam",
                IntuneChannel = "None", Coverage = "OnlyMam", Confidence = "Low", UpdatedAt = input.Now, OperatingSystem = device.Key.Platform, OsVersion = first.PlatformVersion,
            };
            Apply(mamOnly, device.ToList());
            assets.Add(mamOnly);
            links.Add(new AssetLink { AssetId = id, Source = "mam", SourceKey = key, Evidence = "mam-registration", Confidence = "Low", Reason = "Aparelho conhecido apenas pela proteção de aplicativos, sem usuário associado." });
        }

        if (orphans.Count > 0)
        {
            review.Add(new ReviewDraft("MamWithoutUser",
                $"{orphans.Count} aparelho(s) com proteção de aplicativos e sem usuário associado no registro. Foram contados como aparelhos próprios (MAM apenas); confirme de quem são.",
                orphans.Take(20).Select(o => $"mam:tag:{o.Key.Item1}").ToList()));
        }

        void Apply(Asset asset, List<MamRegistrationRecord> regs)
        {
            asset.HasMam = true;
            asset.MamAppCount = regs.Count;
            asset.MamLastSyncAt = regs.Max(r => r.LastSyncAt);
            asset.MamPolicies = string.Join("; ", regs.SelectMany(r => (r.AppliedPolicies ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries)).Distinct().Take(8));
            ActivityModel.Apply(asset, input.Now, input.ActivityWindow);
            if (asset is { InSccm: false, InIntune: false })
            {
                asset.Coverage = "OnlyMam";
            }
        }
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

        return asset.InSccm || asset.InAd ? ("Corporate", "domain") : asset.InXdr ? ("Corporate", "xdr") : asset.InNetskope ? ("Corporate", "netskope") : ("Unknown", null);
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
        NetskopeClientRecord ns => ns.Serial,
        _ => null,
    };

    private static string NameOf(Node n) => n.Record switch
    {
        SccmDeviceRecord s => s.Name ?? "",
        IntuneDeviceRecord i => i.DeviceName ?? "",
        EntraDeviceRecord e => e.DisplayName ?? "",
        AdComputerRecord a => a.Name,
        XdrEndpointRecord x => x.HostName ?? "",
        NetskopeClientRecord ns => ns.HostName ?? "",
        _ => "",
    };

    /// <summary>Host name without the DNS suffix, lower case: "PC-01.corp.example" and "pc-01" are the same name.</summary>
    private static string NameKey(string name) => name.Trim().Split('.')[0].ToLowerInvariant();

    private static string? ShortName(string? host) => string.IsNullOrWhiteSpace(host) ? null : host.Trim().Split('.')[0];

    public static bool IsXdrConnected(string? status) => string.Equals(status?.Trim(), "CONNECTED", StringComparison.OrdinalIgnoreCase);

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
        var makers = members.Select(m => Norm(m.Record switch { SccmDeviceRecord s => s.Manufacturer, IntuneDeviceRecord i => i.Manufacturer, NetskopeClientRecord n => n.Manufacturer, _ => null }))
            .Where(v => v is not null).Select(v => v!.Split(' ', ',', '.')[0]).Distinct().ToArray();
        return makers.Length <= 1;
    }
}
