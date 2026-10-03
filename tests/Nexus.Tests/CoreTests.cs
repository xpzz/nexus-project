using Nexus.Core.Azure;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Core.Setup;

namespace Nexus.Tests;

public class PauseWindowTests
{
    [Theory]
    [InlineData("2026-10-05 01:30", true)]   // Monday, inside
    [InlineData("2026-10-05 03:00", false)]  // end is exclusive
    [InlineData("2026-10-05 00:59", false)]
    public void SameDayWindow(string at, bool expected)
    {
        var window = new PauseWindow { Name = "Backup", Start = new TimeOnly(1, 0), End = new TimeOnly(3, 0) };
        Assert.Equal(expected, window.Contains(DateTime.Parse(at)));
    }

    [Theory]
    [InlineData("2026-10-03 23:30", true)]   // Saturday night
    [InlineData("2026-10-04 01:00", true)]   // Sunday early: belongs to Saturday's window
    [InlineData("2026-10-04 23:30", false)]  // Sunday night: window is only on Saturday
    [InlineData("2026-10-05 01:00", false)]
    public void WindowCrossingMidnightRespectsStartDay(string at, bool expected)
    {
        var window = new PauseWindow { Name = "Manutenção SQL", Days = [DayOfWeek.Saturday], Start = new TimeOnly(22, 0), End = new TimeOnly(2, 0) };
        Assert.Equal(expected, window.Contains(DateTime.Parse(at)));
    }
}

public class SetupCodeTests
{
    [Fact]
    public void CodeHasReadableFormatAndMatchesIgnoringCaseAndDashes()
    {
        var code = SetupCode.Generate();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.True(SetupCode.Matches(code.ToLowerInvariant().Replace("-", " "), SetupCode.Hash(code)));
        Assert.False(SetupCode.Matches("AAAA-BBBB-CCCC", SetupCode.Hash(code)));
    }
}

public class ErrorCatalogTests
{
    [Fact]
    public void EveryErrorSaysWhatHappenedImpactAndHowToFix()
    {
        var errors = ErrorCatalog.All().ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.WhatHappened), e.Code);
            Assert.False(string.IsNullOrWhiteSpace(e.Impact), e.Code);
            Assert.False(string.IsNullOrWhiteSpace(e.HowToFix), e.Code);
        });
        Assert.Equal(errors.Count, errors.Select(e => e.Code).Distinct().Count());
    }
}

public class GraphPermissionTests
{
    public static IEnumerable<object[]> Phase1Permissions() =>
        GraphPermissions.RequiredUpTo(1).Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(Phase1Permissions))]
    public void ValidationPointsExactlyTheRemovedPermission(string removed)
    {
        var required = GraphPermissions.RequiredUpTo(1);
        var granted = required.Select(p => p.Name).Where(n => n != removed);
        var missing = GraphPermissions.Missing(granted, required);
        Assert.Equal(removed, Assert.Single(missing).Name);
    }

    [Fact]
    public void OptionalPermissionsAreNotRequiredByDefault() =>
        Assert.DoesNotContain(GraphPermissions.RequiredUpTo(2), p => p.Optional);
}

public class SettingsStoreTests
{
    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var paths = new Nexus.Core.NexusPaths(Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N")));
        var store = new SettingsStore(paths);
        var settings = new NexusSettings();
        settings.Sccm.SiteCode = "AZ1";
        settings.Collection.PauseWindows.Add(new PauseWindow { Name = "Backup", Start = new TimeOnly(1, 0), End = new TimeOnly(3, 0) });
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal("AZ1", loaded.Sccm.SiteCode);
        Assert.Equal(new TimeOnly(3, 0), Assert.Single(loaded.Collection.PauseWindows).End);
        Directory.Delete(paths.DataDirectory, recursive: true);
    }

    [Fact]
    public void ExportNeverContainsSecretsAndImportKeepsLocalSecret()
    {
        var settings = new NexusSettings();
        settings.Database.ProtectedPassword = "SEGREDO-PROTEGIDO";
        var exported = SettingsStore.Export(settings);
        Assert.DoesNotContain("SEGREDO-PROTEGIDO", exported);

        var local = new NexusSettings();
        local.Database.ProtectedPassword = "SEGREDO-LOCAL";
        var imported = SettingsStore.Import(exported, local);
        Assert.Equal("SEGREDO-LOCAL", imported.Database.ProtectedPassword);
    }
}
