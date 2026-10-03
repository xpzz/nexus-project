using Nexus.Cli;
using Nexus.Collectors.Sccm;
using Nexus.Core.Configuration;

namespace Nexus.Tests;

public class SccmGrantScriptTests
{
    [Fact]
    public void GrantsSelectOnEveryViewThroughDedicatedRoleOnly()
    {
        var script = SccmGrantScript.Grant("CM_AZ1", @"NT SERVICE\AzulNexus.Worker");
        foreach (var view in SccmViews.All)
        {
            Assert.Contains($"GRANT SELECT ON [dbo].[{view}] TO [{SccmViews.ReaderRole}]", script);
        }

        Assert.DoesNotContain("db_owner", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT INSERT", script, StringComparison.OrdinalIgnoreCase);
        // Idempotent guards
        Assert.Contains("IF SUSER_ID(", script);
        Assert.Contains("IF USER_ID(", script);
        Assert.Contains("IF DATABASE_PRINCIPAL_ID(", script);
    }

    [Fact]
    public void RevokeUndoesTheGrant()
    {
        var script = SccmGrantScript.Revoke("CM_AZ1", @"AZUL\svc-nexus$");
        Assert.Contains("DROP USER", script);
        Assert.Contains("DROP ROLE", script);
    }

    [Theory]
    [InlineData("CM_AZ1]; DROP DATABASE x; --", @"AZUL\svc")]
    [InlineData("CM_AZ1", @"AZUL\svc]; DROP LOGIN sa; --")]
    [InlineData("CM_AZ1", "semdominio")]
    public void RejectsInjection(string database, string account) =>
        Assert.Throws<ArgumentException>(() => SccmGrantScript.Grant(database, account));
}

public class DatabaseScriptTests
{
    [Fact]
    public void NexusDatabaseGrantsAreReadWriteWithoutDdl()
    {
        var script = DatabaseScripts.SqlServerGrants("AzulNexus", [@"IIS APPPOOL\AzulNexus", @"NT SERVICE\AzulNexus.Worker"]);
        Assert.Contains("db_datareader", script);
        Assert.Contains("db_datawriter", script);
        Assert.DoesNotContain("db_owner", script);
        Assert.DoesNotContain("db_ddladmin", script);
    }
}

public class InstallAnswersTests
{
    [Fact]
    public void DemoModeUsesSimulatedSources()
    {
        var answers = new InstallAnswers { DemoMode = true };
        answers.Sccm.Mode = SourceMode.Live;
        var settings = answers.ApplyTo(new NexusSettings());
        Assert.Equal(SourceMode.Simulated, settings.Sccm.Mode);
        Assert.Equal(SourceMode.Simulated, settings.ActiveDirectory.Mode);
    }

    [Fact]
    public void ReapplyingTheSameAnswersGivesTheSameConfiguration()
    {
        var answers = new InstallAnswers { PublicUrl = "https://nexus.azul.local:8443" };
        answers.Database.ConnectionString = "Server=SQL01;Database=AzulNexus;Integrated Security=true";
        answers.Sccm.Mode = SourceMode.Live;
        answers.Sccm.SqlServer = "SCCM01";
        answers.Sccm.Database = "CM_AZ1";

        var once = SettingsStore.Export(answers.ApplyTo(new NexusSettings()));
        var twice = SettingsStore.Export(answers.ApplyTo(answers.ApplyTo(new NexusSettings())));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ReapplyingAnswersKeepsSettingsTheyDoNotCover()
    {
        var current = new NexusSettings();
        current.Collection.PauseWindows.Add(new PauseWindow { Name = "Backup" });
        var settings = new InstallAnswers().ApplyTo(current);
        Assert.Single(settings.Collection.PauseWindows);
    }
}
