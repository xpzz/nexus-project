using System.Runtime.CompilerServices;
using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Graph;
using Nexus.Collectors.Sccm;

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
    }

    public List<SccmSystem> SccmSystems { get; } = [];
    public List<AdComputer> AdComputers { get; } = [];
    public List<IntuneManagedDevice> IntuneDevices { get; } = [];
    public List<EntraDevice> EntraDevices { get; } = [];
    public Dictionary<int, string> Serials { get; } = [];

    public ISccmReader CreateSccmReader() => new InMemorySccmReader(SccmSystems);

    public IDirectoryReader CreateDirectoryReader() => new FakeDirectoryReader(AdComputers);

    public IGraphReader CreateGraphReader() => new FakeGraphReader(IntuneDevices, EntraDevices);

    private static Guid GuidFrom(int seed, int index, int salt)
    {
        var bytes = new byte[16];
        new Random(HashCode.Combine(seed, index, salt)).NextBytes(bytes);
        return new Guid(bytes);
    }

    private sealed class InMemorySccmReader(IEnumerable<SccmSystem> systems) : ISccmReader
    {
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
