using System.Runtime.CompilerServices;
using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Sccm;

namespace Nexus.Simulation;

/// <summary>
/// Deterministic synthetic estate shared by tests, the SCCM SQL simulator and demo mode.
/// Includes the edge cases that reconciliation must handle (SPEC §7.2 and §12 Phase 1).
/// </summary>
public sealed class SyntheticEstate
{
    public const string Domain = "corp.azul.sim";

    public static readonly string[] InvalidSerials =
        ["To be filled by O.E.M.", "Default string", "System Serial Number", "0000000000", "None"];

    public SyntheticEstate(int deviceCount = 500, int seed = 20261003)
    {
        var random = new Random(seed);
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        for (var i = 1; i <= deviceCount; i++)
        {
            var name = $"AZ-{(i % 7 == 0 ? "SRV" : "NB")}-{i:D5}";
            var objectGuid = GuidFrom(seed, i, 1);
            var aadDeviceId = i % 5 == 0 ? (Guid?)null : GuidFrom(seed, i, 2);
            var hasClient = i % 11 != 0;          // discovered without client
            var inAd = i % 13 != 0;               // only in SCCM
            var inSccm = i % 17 != 0;             // only in AD
            var stale = i % 9 == 0;               // no recent communication
            var serial = i % 19 == 0 ? InvalidSerials[i % InvalidSerials.Length] : $"SN{random.Next(100000, 999999)}{i:D4}";
            var os = i % 7 == 0 ? "Microsoft Windows Server 2022 Datacenter" : "Microsoft Windows 11 Enterprise";

            if (inSccm)
            {
                SccmSystems.Add(new SccmSystem(
                    16777216 + i, name, "CORP", hasClient, hasClient && !stale, false, aadDeviceId,
                    $"SMBIOS-{GuidFrom(seed, i, 3):N}".ToUpperInvariant(), os));
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
        }

        // Edge cases: renamed device (same AAD id, new name) and obsolete record after reimage.
        var renamed = SccmSystems[3];
        SccmSystems.Add(renamed with { ResourceId = 16777216 + deviceCount + 1, Name = renamed.Name + "-NOVO", Obsolete = false });
        SccmSystems[3] = renamed with { Obsolete = true, Active = false };
    }

    public List<SccmSystem> SccmSystems { get; } = [];
    public List<AdComputer> AdComputers { get; } = [];
    public Dictionary<int, string> Serials { get; } = [];

    public ISccmReader CreateSccmReader() => new InMemorySccmReader(SccmSystems);

    public IDirectoryReader CreateDirectoryReader() => new FakeDirectoryReader(AdComputers);

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
