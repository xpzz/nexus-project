using System.Runtime.CompilerServices;
using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Graph;
using Nexus.Collectors.Sccm;
using Nexus.Collectors.Netskope;
using Nexus.Collectors.Xdr;

namespace Nexus.Simulation;

/// <summary>
/// Deterministic synthetic estate shared by tests, the SCCM SQL simulator and demo mode.
/// Includes the edge cases that reconciliation must handle (SPEC §7.2 and §12 Phase 1): renamed device,
/// reimage with an obsolete record, invalid serials, cloned VMs, re-enrollment, tenant attach without MDM,
/// BYOD, devices only in one source and devices that stopped communicating.
/// </summary>
public sealed class SyntheticEstate
{
    public const string Domain = "corp.azul.sim";

    public static readonly string[] InvalidSerials =
        ["To be filled by O.E.M.", "Default string", "System Serial Number", "0000000000", "None"];

    private static readonly (string Maker, string[] Models)[] Hardware =
    [
        ("Dell Inc.", ["Latitude 5440", "OptiPlex 7010"]),
        ("LENOVO", ["ThinkPad T14", "ThinkCentre M70"]),
        ("HP", ["EliteBook 840", "ProDesk 600"]),
    ];

    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public SyntheticEstate(int deviceCount = 500, int seed = 20261003)
    {
        var random = new Random(seed);
        var start = Now;

        for (var i = 1; i <= deviceCount; i++)
        {
            var isServer = i % 7 == 0;
            var name = $"AZ-{(isServer ? "SRV" : "NB")}-{i:D5}";
            var objectGuid = GuidFrom(seed, i, 1);
            var aadDeviceId = i % 5 == 0 ? (Guid?)null : GuidFrom(seed, i, 2);
            var hasClient = i % 11 != 0;          // discovered without client
            var inAd = i % 13 != 0;               // only in SCCM
            var inSccm = i % 17 != 0;             // only in AD / Intune / Entra
            var stale = i % 9 == 0;               // no recent communication
            var invalidSerial = i % 19 == 0;
            var serial = invalidSerial ? InvalidSerials[i % InvalidSerials.Length] : $"SN{random.Next(100000, 999999)}{i:D4}";
            var win10 = !isServer && i % 6 == 0;          // still on Windows 10 (out of support)
            var os = isServer ? "Microsoft Windows Server 2022 Datacenter" : win10 ? "Microsoft Windows 10 Enterprise" : "Microsoft Windows 11 Enterprise";
            var (maker, models) = Hardware[i % Hardware.Length];
            var model = models[i % models.Length];
            var lastActive = stale ? start.AddDays(-random.Next(60, 120)) : start.AddHours(-random.Next(1, 72));

            if (inSccm)
            {
                SccmSystems.Add(new SccmSystem(
                    16777216 + i, name, "CORP", hasClient, hasClient && !stale, false, aadDeviceId,
                    $"SMBIOS-{GuidFrom(seed, i, 3):N}".ToUpperInvariant(), os, serial, maker, model,
                    hasClient ? lastActive : null, hasClient ? (stale ? 0 : 1) : null));
                Serials[16777216 + i] = serial;
            }

            if (inAd)
            {
                var lastLogon = stale ? start.AddDays(-random.Next(60, 400)) : start.AddDays(-random.Next(0, 14));
                AdComputers.Add(new AdComputer(
                    objectGuid, name, $"{name.ToLowerInvariant()}.{Domain}", os, "10.0 (22631)",
                    lastLogon, lastLogon.AddDays(-random.Next(0, 30)), start.AddDays(-random.Next(200, 1500)),
                    Enabled: i % 23 != 0, $"CN={name},OU=Computadores,DC=corp,DC=azul,DC=sim"));
            }

            // Intune: most Windows clients are co-managed; servers are not enrolled; some cloud-only devices have no SCCM.
            var cloudOnly = !inSccm && i % 3 == 0;
            var inIntune = !isServer && i % 4 != 0 && (inSccm || cloudOnly);
            if (inIntune)
            {
                var agent = i % 29 == 0 ? "configurationManagerClient"                 // tenant attach, no MDM
                    : i % 31 == 0 ? "msSense"                                           // Defender security management
                    : i % 2 == 0 ? "configurationManagerClientMdm" : "mdm";
                IntuneDevices.Add(new IntuneManagedDevice(
                    GuidFrom(seed, i, 4).ToString(), name, aadDeviceId ?? (cloudOnly ? GuidFrom(seed, i, 2) : null), serial, maker, model,
                    "Windows", win10 ? "10.0.19045" : "10.0.22631", agent, "windowsAzureADJoin", "company",
                    stale ? start.AddDays(-random.Next(45, 100)) : start.AddMinutes(-random.Next(5, 2000)),
                    start.AddDays(-random.Next(30, 900)), i % 6 == 0 ? "noncompliant" : "compliant",
                    $"user{i}@corp.azul.sim", GuidFrom(seed, i, 5).ToString()));
            }

            var entraId = aadDeviceId ?? (cloudOnly ? GuidFrom(seed, i, 2) : null);
            if (entraId is not null && !isServer)
            {
                EntraDevices.Add(new EntraDevice(
                    GuidFrom(seed, i, 6).ToString(), entraId, name, inAd ? "ServerAd" : "AzureAd",
                    stale ? start.AddDays(-random.Next(60, 200)) : start.AddHours(-random.Next(1, 96)), true,
                    "Windows", "10.0.22631", "Company", start.AddDays(-random.Next(30, 900))));
            }
        }

        // Renamed device (same AAD id, new name) and obsolete record after reimage.
        var renamed = SccmSystems[3];
        SccmSystems.Add(renamed with { ResourceId = 16777216 + deviceCount + 1, Name = renamed.Name + "-NOVO", Obsolete = false });
        SccmSystems[3] = renamed with { Obsolete = true, Active = false };

        // Cloned VMs: two active SCCM records sharing one SMBIOS GUID, no usable serial, no Entra id.
        for (var k = 0; k < 2; k++)
        {
            SccmSystems.Add(new SccmSystem(16777216 + deviceCount + 10 + k, $"AZ-VM-CLONE-{k + 1}", "CORP", true, true, false, null,
                "SMBIOS-CLONEDCLONEDCLONEDCLONEDCLONED00", "Microsoft Windows Server 2022 Standard", "0000000000", "Microsoft Corporation", "Virtual Machine", Now.AddHours(-3), 1));
        }

        // Re-enrollment: two Intune records with the same serial; only the new one has the Entra id the SCCM record carries.
        var reenroll = SccmSystems[10];
        if (reenroll.AadDeviceId is { } reenrollAad)
        {
            IntuneDevices.Add(new IntuneManagedDevice("reenroll-old", "OLD-" + reenroll.Name, GuidFrom(seed, 999, 7), reenroll.Serial, reenroll.Manufacturer, reenroll.Model,
                "Windows", "10.0.19045", "mdm", "windowsAzureADJoin", "company", Now.AddDays(-120), Now.AddDays(-800), "compliant", null, null));
            IntuneDevices.Add(new IntuneManagedDevice("reenroll-new", reenroll.Name, reenrollAad, reenroll.Serial, reenroll.Manufacturer, reenroll.Model,
                "Windows", "10.0.22631", "mdm", "windowsAzureADJoin", "company", Now.AddHours(-2), Now.AddDays(-30), "compliant", null, null));
        }

        // External devices registered in Entra ID only (guests and contractors): registered is not managed.
        for (var x = 0; x < 20; x++)
        {
            EntraDevices.Add(new EntraDevice(GuidFrom(seed, 7000 + x, 10).ToString(), GuidFrom(seed, 7000 + x, 11), $"EXT-{x + 1:D3}", "Workplace",
                Now.AddDays(-random.Next(0, 120)), true, x % 4 == 0 ? "Android" : "Windows", x % 4 == 0 ? "14" : "10.0.22631", "Personal", Now.AddDays(-random.Next(30, 600))));
        }

        // Mobile devices only in Intune: 40 corporate and 60 personal (BYOD).
        for (var m = 0; m < 100; m++)
        {
            var personal = m >= 40;
            var ios = m % 2 == 0;
            IntuneDevices.Add(new IntuneManagedDevice(GuidFrom(seed, 5000 + m, 8).ToString(), $"{(ios ? "iPhone" : "Android")}-{m:D3}", GuidFrom(seed, 5000 + m, 9),
                $"MOB{random.Next(100000, 999999)}{m}", ios ? "Apple" : "Samsung", ios ? "iPhone 14" : "Galaxy S23", ios ? "iOS" : "Android", ios ? "17.5" : "14",
                "mdm", personal ? "userEnrollment" : "appleBulkWithUser", personal ? "personal" : "company", Now.AddHours(-random.Next(1, 200)), Now.AddDays(-random.Next(10, 400)),
                "compliant", $"mobile{m}@corp.azul.sim", null));
        }

        Enrich(seed);
    }

    public List<XdrEndpoint> XdrEndpoints { get; } = [];
    public List<NetskopeClient> NetskopeClients { get; } = [];
    public List<IntunePolicy> Policies { get; } = [];
    public List<DevicePolicyState> PolicyStates { get; } = [];
    public List<MamRegistration> MamRegistrations { get; } = [];
    public List<AppProtectionPolicyInfo> AppProtectionPolicies { get; } = [];
    public List<AppConfigInfo> AppConfigs { get; } = [];
    public List<ConditionalAccessInfo> ConditionalAccessPolicies { get; } = [];
    public List<SignInAccess> SignIns { get; } = [];
    public List<EntraUser> Users { get; } = [];
    public Dictionary<string, List<DetectedApp>> DetectedApps { get; } = [];

    public List<SccmSystem> SccmSystems { get; } = [];
    public List<AdComputer> AdComputers { get; } = [];
    public List<IntuneManagedDevice> IntuneDevices { get; } = [];
    public List<EntraDevice> EntraDevices { get; } = [];
    public Dictionary<int, string> Serials { get; } = [];

    public IXdrReader CreateXdrReader() => new FakeXdrReader(XdrEndpoints);

    public INetskopeReader CreateNetskopeReader() => new FakeNetskopeReader(NetskopeClients);

    public ISccmReader CreateSccmReader() => new InMemorySccmReader(SccmSystems, SoftwareFor, SccmExtrasById);

    public IDirectoryReader CreateDirectoryReader() => new FakeDirectoryReader(AdComputers);

    public IGraphGovernanceReader CreateGovernanceReader() => new FakeGovernanceReader
    {
        Protection = AppProtectionPolicies, Configs = AppConfigs, ConditionalAccess = ConditionalAccessPolicies, SignIns = SignIns,
    };

    public IGraphReader CreateGraphReader() => new FakeGraphReader(IntuneDevices, EntraDevices)
    {
        Policies = Policies, PolicyStates = PolicyStates, MamRegistrations = MamRegistrations, Users = Users, DetectedApps = DetectedApps,
    };

    /// <summary>Installed programs for one SCCM device: a common base plus a few that depend on the device (deterministic).</summary>
    public IReadOnlyList<SccmSoftware> SoftwareFor(int resourceId)
    {
        var r = new Random(HashCode.Combine(resourceId, 77));
        var list = new List<SccmSoftware>
        {
            new("Microsoft 365 Apps for enterprise", "16.0.17928", "Microsoft Corporation", Now.AddDays(-r.Next(30, 300))),
            new("Microsoft Edge", "129.0.2792.52", "Microsoft Corporation", Now.AddDays(-r.Next(1, 20))),
            new("Microsoft Teams", "24243.1309", "Microsoft Corporation", Now.AddDays(-r.Next(1, 40))),
            new("Configuration Manager Client", "5.00.9128.1000", "Microsoft Corporation", Now.AddDays(-r.Next(60, 500))),
            new("Microsoft Defender for Endpoint", "10.8210", "Microsoft Corporation", Now.AddDays(-r.Next(60, 500))),
            new("7-Zip", "23.01", "Igor Pavlov", Now.AddDays(-r.Next(60, 800))),
        };
        foreach (var extra in new[] { ("Google Chrome", "129.0.6668.71", "Google LLC"), ("Adobe Acrobat Reader", "24.003.20112", "Adobe"), ("Zoom Workplace", "6.2.0", "Zoom Video Communications"),
                     ("VLC media player", "3.0.21", "VideoLAN"), ("Notepad++", "8.6.9", "Notepad++ Team"), ("Java 8 Update 421", "8.0.4210.9", "Oracle Corporation"), ("AnyDesk", "8.0.15", "philandro Software") })
        {
            if (r.NextDouble() < .45)
            {
                list.Add(new SccmSoftware(extra.Item1, extra.Item2, extra.Item3, Now.AddDays(-r.Next(5, 900))));
            }
        }

        return list.OrderBy(x => x.Name).ToList();
    }

    private static int StableHash(string text) => text.Aggregate(17, (h, c) => unchecked(h * 31 + c));

    private void Enrich(int seed)
    {
        var random = new Random(seed ^ 0x5EED); // separate stream: the original estate stays exactly as it was

        // SCCM: client details and hardware.
        var cpus = new[] { ("Intel(R) Core(TM) i5-1235U", 10), ("Intel(R) Core(TM) i7-1355U", 10), ("AMD Ryzen 5 PRO 6650U", 6), ("Intel(R) Xeon(R) Silver 4310", 12) };
        for (var i = 0; i < SccmSystems.Count; i++)
        {
            var s = SccmSystems[i];
            var server = s.OperatingSystem?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true;
            var win10 = s.OperatingSystem?.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) == true;
            var cpu = server ? cpus[3] : cpus[random.Next(0, 3)];
            var memory = server ? new[] { 32768, 65536 }[random.Next(2)] : new[] { 8192, 16384, 32768 }[random.Next(3)];
            var disk = server ? 512000 : new[] { 238000, 476000 }[random.Next(2)];
            var active = s.LastActiveAt;
            SccmSystems[i] = s with
            {
                ClientVersion = s.Client == true ? "5.00.9128.1000" : null,
                LastPolicyRequestAt = active?.AddMinutes(-random.Next(1, 90)),
                LastDdrAt = active?.AddHours(-random.Next(0, 20)),
                LastHwScanAt = active?.AddDays(-random.Next(0, 6)),
                LastSwScanAt = active?.AddDays(-random.Next(0, 9)),
                LastLogonUser = server ? null : $"CORP\\user{s.ResourceId % 997}",
                AdSite = new[] { "Barueri", "Campinas", "Recife", "Porto-Alegre" }[random.Next(4)],
                OsVersion = server ? "10.0.20348" : win10 ? "10.0.19045" : "10.0.22631",
                LastBootAt = active?.AddDays(-random.Next(0, 25)),
                CpuName = cpu.Item1,
                CpuCores = cpu.Item2,
                MemoryMb = memory,
                DiskTotalMb = disk,
                DiskFreeMb = (long)(disk * (0.08 + random.NextDouble() * 0.7)),
                BiosVersion = $"{s.Manufacturer?.Split(' ')[0]} {random.Next(1, 3)}.{random.Next(0, 30)}.{random.Next(0, 9)}",
            };
        }

        // Cortex XDR: the table holds most corporate Windows machines. Names come short or as FQDN; a few agents are disconnected,
        // a few machines have two agents (reinstall) and a few exist only in the XDR.
        var xdrRandom = new Random(seed ^ 0xC0DE);
        string XdrHost(string name) => xdrRandom.NextDouble() < .2 ? $"{name.ToLowerInvariant()}.corp.azul.sim" : name;
        void AddXdr(string host, bool server, DateTimeOffset? lastSeen, string status, string? user)
        {
            XdrEndpoints.Add(new XdrEndpoint(Convert.ToHexString(BitConverter.GetBytes(HashCode.Combine(seed, host, XdrEndpoints.Count))).ToLowerInvariant().PadRight(32, '0'),
                host, status, status == "CONNECTED" ? "PROTECTED" : "UNPROTECTED", server ? "AGENT_TYPE_SERVER" : "AGENT_TYPE_WORKSTATION",
                $"10.{xdrRandom.Next(1, 40)}.{xdrRandom.Next(0, 250)}.{xdrRandom.Next(2, 250)}", lastSeen, user));
        }

        foreach (var sys in SccmSystems.Where(x => x.Obsolete != true && x.Name is not null && x.OperatingSystem?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true))
        {
            var server = sys.OperatingSystem!.Contains("Server", StringComparison.OrdinalIgnoreCase);
            if (xdrRandom.NextDouble() > (server ? .93 : .87))
            {
                continue; // no agent installed
            }

            var roll = xdrRandom.NextDouble();
            var seen = sys.LastActiveAt ?? Now.AddDays(-60);
            if (roll < .04)
            {
                AddXdr(XdrHost(sys.Name!), server, Now.AddDays(-xdrRandom.Next(10, 40)), "DISCONNECTED", server ? null : sys.LastLogonUser);
            }
            else if (roll < .06)
            {
                AddXdr(XdrHost(sys.Name!), server, Now.AddDays(-xdrRandom.Next(40, 120)), "LOST", server ? null : sys.LastLogonUser);
            }
            else
            {
                AddXdr(XdrHost(sys.Name!), server, seen.AddMinutes(-xdrRandom.Next(1, 600)), "CONNECTED", server ? null : sys.LastLogonUser);
            }

            if (xdrRandom.NextDouble() < .01)
            {
                AddXdr(sys.Name!, server, Now.AddDays(-xdrRandom.Next(60, 200)), "LOST", null); // old agent after a reinstall
            }
        }

        foreach (var ad in AdComputers.Where(a => a.Name.Contains("-OLD-", StringComparison.Ordinal) || xdrRandom.NextDouble() < .02).Take(30))
        {
            AddXdr(ad.Name, false, Now.AddDays(-xdrRandom.Next(0, 90)), "CONNECTED", null);
        }

        for (var k = 1; k <= 8; k++)
        {
            AddXdr($"AZ-XDR-ONLY-{k:D2}", k % 3 == 0, Now.AddHours(-xdrRandom.Next(1, 300)), "CONNECTED", $"CORP\\xdr{k}");
        }

        // Netskope: most corporate laptops run the client. Its management id is the Entra device id for some, the serial number joins others,
        // and the rest only share the host name. A few clients are active while SCCM is silent (the divergence the active pool is meant to expose).
        var nsRandom = new Random(seed ^ 0x5E75);
        var nsCounter = 0;
        void AddNetskope(string host, string os, string osVersion, string? serial, string? maker, string? model, DateTimeOffset? lastEvent, string status, string? managementId, string? user)
        {
            NetskopeClients.Add(new NetskopeClient($"ns-{++nsCounter:D6}", Guid.NewGuid().ToString(), host, os, osVersion, serial, maker, model, "126.0.2.1" + nsRandom.Next(0, 9),
                status, lastEvent, Now.AddDays(-nsRandom.Next(30, 700)), managementId, user));
        }

        foreach (var sys in SccmSystems.Where(x => x.Obsolete != true && x.Name is not null && x.OperatingSystem?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true && x.OperatingSystem.Contains("Server", StringComparison.OrdinalIgnoreCase) == false))
        {
            if (nsRandom.NextDouble() > .82)
            {
                continue;
            }

            var seen = nsRandom.NextDouble() < .08
                ? Now.AddHours(-nsRandom.Next(1, 48))                       // active in Netskope even if SCCM is silent
                : (sys.LastActiveAt ?? Now.AddDays(-80)).AddMinutes(-nsRandom.Next(1, 900));
            var roll = nsRandom.NextDouble();
            var host = nsRandom.NextDouble() < .25 ? $"{sys.Name!.ToLowerInvariant()}.corp.azul.sim" : sys.Name!;
            var byId = roll < .4 && sys.AadDeviceId is { } aad ? aad.ToString() : null;
            var bySerial = byId is null && roll < .75 ? sys.Serial : null;
            AddNetskope(host, sys.OperatingSystem!.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ? "Windows 10" : "Windows 11", sys.OsVersion ?? "10.0.22631",
                bySerial, bySerial is null ? null : sys.Manufacturer, bySerial is null ? null : sys.Model, seen, nsRandom.NextDouble() < .03 ? "Disabled" : "Enabled", byId, sys.LastLogonUser);
            if (nsRandom.NextDouble() < .01)
            {
                AddNetskope(host, "Windows 10", "10.0.19045", null, null, null, Now.AddDays(-nsRandom.Next(60, 200)), "Disabled", null, null); // older install of the same host
            }
        }

        var sccmAadIds = SccmSystems.Where(x => x.AadDeviceId is not null).Select(x => x.AadDeviceId!.Value).ToHashSet();
        foreach (var mac in IntuneDevices.Where(d => (d.OperatingSystem == "macOS" || d.OperatingSystem == "Windows") && (d.AzureAdDeviceId is null || !sccmAadIds.Contains(d.AzureAdDeviceId.Value))).Take(40))
        {
            if (nsRandom.NextDouble() < .7)
            {
                AddNetskope(mac.DeviceName ?? "host", mac.OperatingSystem!, mac.OsVersion ?? "", mac.SerialNumber, mac.Manufacturer, mac.Model, mac.LastSyncAt?.AddMinutes(-nsRandom.Next(1, 600)), "Enabled",
                    mac.AzureAdDeviceId?.ToString(), mac.UserPrincipalName);
            }
        }

        for (var k = 1; k <= 6; k++)
        {
            AddNetskope($"AZ-NS-ONLY-{k:D2}", "Windows 11", "10.0.22631", null, null, null, Now.AddHours(-nsRandom.Next(1, 200)), "Enabled", null, $"ns{k}@corp.azul.sim");
        }

        // Entra users and Intune device extras.
        var userIds = new Dictionary<string, EntraUser>();
        var depts = new[] { "Operações", "Comercial", "Finanças", "TI", "RH", "Manutenção", "Logística", "Jurídico" };
        for (var i = 0; i < IntuneDevices.Count; i++)
        {
            var d = IntuneDevices[i];
            var userId = d.UserId ?? GuidFrom(seed, 9000 + i, 12).ToString();
            var windows = d.OperatingSystem == "Windows";
            var mobile = d.OperatingSystem is "iOS" or "Android";
            var total = windows ? 476_000_000_000L : 128_000_000_000L;
            IntuneDevices[i] = d with
            {
                UserId = userId,
                IsEncrypted = windows ? random.NextDouble() < .92 : true,
                JailBroken = mobile ? (random.NextDouble() < .02 ? "True" : "False") : "Unknown",
                IsSupervised = d.OperatingSystem == "iOS" ? d.OwnerType == "company" : null,
                TotalStorageBytes = total,
                FreeStorageBytes = (long)(total * (0.1 + random.NextDouble() * .6)),
                PhysicalMemoryBytes = windows ? 16L * 1024 * 1024 * 1024 : 6L * 1024 * 1024 * 1024,
                DeviceRegistrationState = "registered",
                AutopilotEnrolled = windows ? random.NextDouble() < .6 : null,
                ComplianceGraceExpiresAt = d.ComplianceState == "noncompliant" ? Now.AddDays(random.Next(1, 20)) : null,
            };
            if (!userIds.ContainsKey(userId))
            {
                userIds[userId] = new EntraUser(userId, d.UserPrincipalName ?? $"user{i}@corp.azul.sim", $"Usuário {i}", depts[random.Next(depts.Length)], random.NextDouble() > .03);
            }
        }

        // Policy catalog.
        IntunePolicy P(string kind, string name, string platform, string assign, bool all = false, int count = 1) =>
            new(kind, GuidFrom(seed, StableHash(name), 13).ToString(), name, null, platform, random.Next(1, 9), Now.AddDays(-random.Next(3, 300)), assign, all, count);
        Policies.AddRange(
        [
            P("compliance", "Windows - Conformidade corporativa", "Windows", "Grupo: Notebooks - Produção; Exclui: Dispositivos de teste", false, 2),
            P("compliance", "macOS - Conformidade corporativa", "macOS", "Grupo: Macs corporativos"),
            P("compliance", "iOS - Conformidade corporativa", "iOS", "Grupo: Celulares corporativos"),
            P("compliance", "Android - Conformidade corporativa", "Android", "Grupo: Celulares corporativos"),
            P("compliance", "Celulares BYOD - Conformidade mínima", "iOS, Android", "Grupo: BYOD"),
            P("configuration", "Windows - BitLocker (criptografia de disco)", "Windows", "Todos os dispositivos", true),
            P("configuration", "Windows - Defender e firewall", "Windows", "Todos os dispositivos", true),
            P("configuration", "Windows - Wi-Fi corporativo", "Windows", "Grupo: Notebooks - Produção"),
            P("configuration", "Windows - Anel de atualização (Produção)", "Windows", "Grupo: Notebooks - Produção; Exclui: Piloto"),
            P("configuration", "iOS - Restrições de dispositivo", "iOS", "Grupo: Celulares corporativos"),
            P("configuration", "Android - Perfil de trabalho", "Android", "Grupo: Celulares corporativos"),
            P("configuration", "macOS - FileVault", "macOS", "Grupo: Macs corporativos"),
            P("settings", "Windows - Linha de base de segurança", "windows10", "Grupo: Notebooks - Produção"),
            P("mam-ios", "iOS - Proteção de apps corporativos", "iOS", "Grupo: BYOD"),
            P("mam-android", "Android - Proteção de apps corporativos", "Android", "Grupo: BYOD"),
            P("mam-windows", "Windows - Proteção de dados (Edge)", "Windows", "Grupo: BYOD"),
        ]);

        // Per-device policy states.
        DevicePolicyState Ps(string device, string kind, string name, string state, string platform) =>
            new(device, kind, GuidFrom(seed, StableHash(name), 13).ToString(), name, state, platform, random.Next(4, 40), 1);
        foreach (var d in IntuneDevices.Where(d => d.ManagementAgent?.Contains("mdm", StringComparison.OrdinalIgnoreCase) == true))
        {
            var failing = d.ComplianceState == "noncompliant";
            var noPolicy = random.NextDouble() < .04 && !failing;
            switch (d.OperatingSystem)
            {
                case "Windows":
                    if (!noPolicy)
                    {
                        PolicyStates.Add(Ps(d.Id, "compliance", "Windows - Conformidade corporativa", failing ? "nonCompliant" : "compliant", "windows10AndLater"));
                    }

                    PolicyStates.Add(Ps(d.Id, "configuration", "Windows - BitLocker (criptografia de disco)", d.IsEncrypted == false ? "error" : "compliant", "windows10AndLater"));
                    PolicyStates.Add(Ps(d.Id, "configuration", "Windows - Defender e firewall", random.NextDouble() < .04 ? "conflict" : "compliant", "windows10AndLater"));
                    PolicyStates.Add(Ps(d.Id, "configuration", "Windows - Wi-Fi corporativo", "compliant", "windows10AndLater"));
                    PolicyStates.Add(Ps(d.Id, "configuration", "Windows - Anel de atualização (Produção)", random.NextDouble() < .05 ? "pending" : "compliant", "windows10AndLater"));
                    break;
                case "iOS":
                case "Android":
                    var ios = d.OperatingSystem == "iOS";
                    var byod = d.OwnerType == "personal";
                    PolicyStates.Add(Ps(d.Id, "compliance", byod ? "Celulares BYOD - Conformidade mínima" : ios ? "iOS - Conformidade corporativa" : "Android - Conformidade corporativa", failing ? "nonCompliant" : "compliant", ios ? "iOS" : "androidForWork"));
                    if (!byod)
                    {
                        PolicyStates.Add(Ps(d.Id, "configuration", ios ? "iOS - Restrições de dispositivo" : "Android - Perfil de trabalho", random.NextDouble() < .06 ? "error" : "compliant", ios ? "iOS" : "androidWorkProfile"));
                    }

                    break;
                default:
                    PolicyStates.Add(Ps(d.Id, "compliance", "macOS - Conformidade corporativa", "compliant", "macOS"));
                    break;
            }
        }

        // MAM: most personal phones also run protected apps; 25 users have MAM and no MDM.
        var apps = new[] { ("com.microsoft.office.outlook", "Outlook"), ("com.microsoft.teams", "Teams"), ("com.microsoft.emmx", "Edge"), ("com.microsoft.skydrive", "OneDrive") };
        var personalPhones = IntuneDevices.Where(d => d.OwnerType == "personal" && d.OperatingSystem is "iOS" or "Android").ToList();
        void Register(string userId, string platform, string deviceName, string tag, int appCount)
        {
            var policy = platform == "iOS" ? "iOS - Proteção de apps corporativos" : platform == "Android" ? "Android - Proteção de apps corporativos" : "Windows - Proteção de dados (Edge)";
            for (var a = 0; a < appCount; a++)
            {
                MamRegistrations.Add(new MamRegistration($"{tag}-{a}", userId, deviceName, tag, platform, apps[a].Item1, $"{random.Next(4, 9)}.{random.Next(0, 99)}.{random.Next(0, 9)}",
                    platform == "iOS" ? "17.5" : "13", Now.AddHours(-random.Next(1, 400)), Now.AddDays(-random.Next(20, 300)),
                    random.NextDouble() < .05 ? "Sistema abaixo da versão mínima" : null, policy, policy));
            }
        }

        foreach (var phone in personalPhones.Where(_ => random.NextDouble() < .7))
        {
            Register(phone.UserId!, phone.OperatingSystem!, phone.DeviceName ?? "BYOD", "tag-" + phone.Id[..8], random.Next(2, 5));
        }

        for (var k = 0; k < 25; k++)
        {
            var userId = GuidFrom(seed, 8000 + k, 14).ToString();
            var ios = k % 2 == 0;
            userIds[userId] = new EntraUser(userId, $"byod{k}@corp.azul.sim", $"Usuário BYOD {k}", depts[random.Next(depts.Length)], true);
            Register(userId, ios ? "iOS" : "Android", ios ? $"iPhone de byod{k}" : $"Android de byod{k}", $"solo-{k}", random.Next(1, 4));
        }

        Users.AddRange(userIds.Values);
        EnrichGovernance(random);
        EnrichNetwork(seed);

        // Applications Intune detected on Windows devices.
        var names = new[] { ("Microsoft 365 Apps", "16.0.17928"), ("Google Chrome", "129.0.6668"), ("Zoom", "6.2.0"), ("Adobe Reader", "24.003"), ("7-Zip", "23.01"), ("Java 8", "8.0.421"), ("Slack", "4.40.133") };
        foreach (var d in IntuneDevices.Where(d => d.OperatingSystem == "Windows"))
        {
            DetectedApps[d.Id] = names.Where(_ => random.NextDouble() < .7).Select(n => new DetectedApp(n.Item1, n.Item2, null, random.Next(20, 900) * 1_000_000L)).ToList();
        }
    }

    /// <summary>Synthetic governance data: protection policies (one with deliberate gaps), Edge URL lists, Conditional Access and Microsoft 365 sign-ins.</summary>
    private void EnrichGovernance(Random random)
    {
        AppProtectionPolicies.Add(new AppProtectionPolicyInfo("00000000-0000-4000-8000-0000000000a1", "iOS", "iOS - Proteção de apps corporativos", Now.AddDays(-40), 5, true, false, "Grupo: BYOD - Celulares",
            ["com.microsoft.office.outlook", "com.microsoft.teams", "com.microsoft.msedge", "com.microsoft.skydrive"],
            new Dictionary<string, string>
            {
                ["allowedOutboundClipboardSharingLevel"] = "managedAppsWithPasteIn", ["allowedOutboundDataTransferDestinations"] = "managedApps", ["allowedInboundDataTransferSources"] = "managedApps",
                ["allowedDataStorageLocations"] = "oneDriveForBusiness,sharePoint", ["dataBackupBlocked"] = "true", ["pinRequired"] = "true", ["minimumPinLength"] = "6",
                ["appDataEncryptionType"] = "whenDeviceLocked", ["periodOfflineBeforeWipeIsEnforced"] = "P90D",
            }));
        AppProtectionPolicies.Add(new AppProtectionPolicyInfo("00000000-0000-4000-8000-0000000000a2", "Android", "Android - Proteção de apps corporativos", Now.AddDays(-200), 2, true, false, "Grupo: BYOD - Celulares",
            ["com.microsoft.office.outlook", "com.microsoft.teams", "com.microsoft.emmx", "com.microsoft.skydrive"],
            new Dictionary<string, string>
            {
                ["allowedOutboundClipboardSharingLevel"] = "allowed", ["allowedOutboundDataTransferDestinations"] = "allApps", ["allowedInboundDataTransferSources"] = "allApps",
                ["allowedDataStorageLocations"] = "oneDriveForBusiness,sharePoint,localStorage", ["dataBackupBlocked"] = "false", ["pinRequired"] = "true", ["minimumPinLength"] = "4",
                ["encryptAppData"] = "true", ["screenCaptureBlocked"] = "false",
            }));
        AppProtectionPolicies.Add(new AppProtectionPolicyInfo("00000000-0000-4000-8000-0000000000a3", "iOS", "iOS - Piloto restrito", Now.AddDays(-5), 1, false, false, "Sem atribuição", ["com.microsoft.office.outlook"],
            new Dictionary<string, string> { ["allowedOutboundClipboardSharingLevel"] = "blocked", ["pinRequired"] = "true", ["dataBackupBlocked"] = "true" }));

        string Sites(int n) => string.Join("|", Enumerable.Range(0, n).Select(i => $"bloqueado{i}.exemplo.sim"));
        AppConfigs.Add(new AppConfigInfo("00000000-0000-4000-8000-0000000000b1", "managed-app", "iOS", "Edge corporativo - iOS", Now.AddDays(-12), "Grupo: BYOD - Celulares", ["com.microsoft.msedge"],
            new Dictionary<string, string> { ["com.microsoft.intune.mam.managedbrowser.BlockListURLs"] = Sites(912), ["com.microsoft.intune.mam.managedbrowser.AllowListURLs"] = "corp.azul.sim|intranet.azul.sim" }));
        AppConfigs.Add(new AppConfigInfo("00000000-0000-4000-8000-0000000000b2", "managed-device", "Android", "Edge - perfil de trabalho Android", Now.AddDays(-30), "Grupo: BYOD - Celulares", [],
            new Dictionary<string, string> { ["URLBlocklist"] = Sites(120), ["URLAllowlist"] = "corp.azul.sim" }));

        ConditionalAccessPolicies.Add(new ConditionalAccessInfo("00000000-0000-4000-8000-0000000000c1", "Exigir app protegido em celulares", "enabled", Now.AddDays(-60), "Todos os usuários", "Microsoft 365", "android, iOS",
            ["approvedApplication", "compliantApplication"], false, true, true, false, true));
        ConditionalAccessPolicies.Add(new ConditionalAccessInfo("00000000-0000-4000-8000-0000000000c2", "Exigir dispositivo compliant em Windows", "enabled", Now.AddDays(-90), "Todos os usuários", "Todos os aplicativos", "windows",
            ["compliantDevice", "mfa"], true, false, false, true, true));
        ConditionalAccessPolicies.Add(new ConditionalAccessInfo("00000000-0000-4000-8000-0000000000c3", "Exigir MFA fora da rede (relatório)", "enabledForReportingButNotEnforced", Now.AddDays(-10), "3 grupo(s)", "Todos os aplicativos", "Qualquer",
            ["mfa"], false, false, false, true, true));

        // Microsoft 365 access: most personal phones and half of the Windows laptops signed in recently; some people use a browser on a device nobody knows.
        var workloads = new[] { "Exchange", "SharePoint", "Teams", "OneDrive" };
        foreach (var d in IntuneDevices.Where(d => d.AzureAdDeviceId is not null && random.NextDouble() < (d.OwnerType == "personal" ? .8 : .5)))
        {
            var mine = workloads.Where(_ => random.NextDouble() < .6).DefaultIfEmpty("Exchange").ToList();
            SignIns.Add(new SignInAccess("dev:" + d.AzureAdDeviceId, d.UserId, d.UserPrincipalName, d.AzureAdDeviceId.ToString(), d.DeviceName, d.OperatingSystem, d.OperatingSystem is "iOS" or "Android" ? "Edge Mobile" : "Edge",
                d.OwnerType == "company", d.ComplianceState == "compliant", d.OwnerType == "company" ? "AzureAd" : "Workplace", Now.AddHours(-random.Next(1, 24 * 12)), string.Join(",", mine), random.Next(1, 90), "Mobile Apps and Desktop clients", random.NextDouble() < .88 ? "success" : random.NextDouble() < .5 ? "failure" : "notApplied"));
        }

        for (var k = 0; k < 18; k++)
        {
            var user = Users[random.Next(Users.Count)];
            var os = k % 3 == 0 ? "Windows" : k % 3 == 1 ? "iOS" : "Android";
            SignIns.Add(new SignInAccess($"anon:{user.Id}|{os}|chrome".ToLowerInvariant(), user.Id, user.UserPrincipalName, null, null, os, "Chrome", null, null, null,
                Now.AddHours(-random.Next(1, 24 * 10)), string.Join(",", workloads.Where(_ => random.NextDouble() < .5).DefaultIfEmpty("Exchange")), random.Next(1, 20), "Browser"));
        }
    }

    private static Guid GuidFrom(int seed, int index, int salt)
    {
        var bytes = new byte[16];
        new Random(HashCode.Combine(seed, index, salt)).NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <summary>Deterministic physical-looking MAC for a key (an Entra device id or a resource id).</summary>
    private static string MacFor(int seed, string key)
    {
        var bytes = new byte[6];
        new Random(HashCode.Combine(seed, key)).NextBytes(bytes);
        bytes[0] = (byte)(0xA4 & 0xFC); // universally administered, unicast
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    public Dictionary<int, SccmExtras> SccmExtrasById { get; } = [];

    private void EnrichNetwork(int seed)
    {
        for (var i = 0; i < SccmSystems.Count; i++)
        {
            var s = SccmSystems[i];
            var key = s.AadDeviceId?.ToString() ?? "rid" + s.ResourceId;
            var chassis = (s.Name ?? "").Contains("-SRV-", StringComparison.OrdinalIgnoreCase) ? ChassisKinds.Server : (s.Name ?? "").Contains("-NB-", StringComparison.OrdinalIgnoreCase) ? ChassisKinds.Laptop : ChassisKinds.Desktop;
            var vm = (s.Name ?? "").Contains("-VM-", StringComparison.OrdinalIgnoreCase);
            SccmExtrasById[s.ResourceId] = new SccmExtras(s.ResourceId, [vm ? "00:15:5D:" + MacFor(seed, key)[9..] : MacFor(seed, key)], [$"10.{seed % 200}.{s.ResourceId % 250}.{s.ResourceId % 200 + 10}"], vm ? null : chassis);
        }

        // Two different physical machines that report the same MAC (a swapped card or a re-imaged laptop): the data-quality page must show it.
        if (SccmSystems.Count > 10)
        {
            var (a, b) = (SccmSystems[3], SccmSystems[7]);
            SccmExtrasById[b.ResourceId] = SccmExtrasById[b.ResourceId] with { Macs = SccmExtrasById[a.ResourceId].Macs };
        }

        for (var i = 0; i < IntuneDevices.Count; i++)
        {
            var d = IntuneDevices[i];
            if (d.OwnerType == "personal" || d.AzureAdDeviceId is null)
            {
                continue;
            }

            IntuneDevices[i] = d with { EthernetMac = MacFor(seed, d.AzureAdDeviceId.ToString()!), WifiMac = MacFor(seed, "wifi" + d.AzureAdDeviceId) };
        }
    }

    private sealed class InMemorySccmReader(IEnumerable<SccmSystem> systems, Func<int, IReadOnlyList<SccmSoftware>> software, IReadOnlyDictionary<int, SccmExtras> extras) : ISccmReader
    {
        public Task<IReadOnlyDictionary<int, SccmExtras>> ReadExtrasAsync(CancellationToken cancellationToken) => Task.FromResult(extras);

        public Task<IReadOnlyList<SccmSoftware>> ReadSoftwareAsync(int resourceId, CancellationToken cancellationToken) => Task.FromResult(software(resourceId));

        public async IAsyncEnumerable<SccmSystem> ReadSystemsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var system in systems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return system;
            }
        }
    }
}
