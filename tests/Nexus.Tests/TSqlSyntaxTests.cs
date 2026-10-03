using Microsoft.SqlServer.TransactSql.ScriptDom;
using Nexus.Cli;
using Nexus.Collectors.Sccm;
using Nexus.Simulation;

namespace Nexus.Tests;

/// <summary>
/// Every T-SQL script the product generates must parse with the official Microsoft T-SQL parser
/// (ScriptDom). Regression: "EXEC (N'...' + QUOTENAME(...))" is invalid and only failed on the customer's SQL Server.
/// </summary>
public class TSqlSyntaxTests
{
    public static IEnumerable<object[]> Scripts()
    {
        const string account = @"AZUL_CORP\svc.sccm";
        yield return ["SCCM grant", SccmGrantScript.Grant("CM_AZ1", account)];
        yield return ["SCCM grant (virtual account)", SccmGrantScript.Grant("CM_AZ1", @"NT SERVICE\AzulNexus.Worker")];
        yield return ["SCCM revoke", SccmGrantScript.Revoke("CM_AZ1", account)];
        yield return ["Nexus DB grants", DatabaseScripts.SqlServerGrants("AzulNexus", [account, @"IIS APPPOOL\AzulNexus"])];
        yield return ["SCCM read query (base)", SqlSccmReader.BaseQuery];
        yield return ["SCCM read query (extended)", SqlSccmReader.ExtendedQuery];
        yield return ["SCCM simulator", SccmSimulatorScript.Create(new SyntheticEstate(20))];
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void ScriptParsesWithoutErrors(string name, string script)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var batches = SplitBatches(script).ToList();
        Assert.NotEmpty(batches);
        foreach (var batch in batches)
        {
            parser.Parse(new StringReader(batch), out var errors);
            Assert.True(errors.Count == 0, $"{name}: {string.Join("; ", errors.Select(e => $"linha {e.Line}: {e.Message}"))}\n--- lote ---\n{batch}");
        }
    }

    [Fact]
    public void TheParserRejectsTheOldInvalidForm()
    {
        // Guard for the guard: proves this test would have caught the original bug.
        const string invalid = "DECLARE @l sysname = N'x'; EXEC (N'CREATE USER ' + QUOTENAME(@l));";
        new TSql160Parser(true).Parse(new StringReader(invalid), out var errors);
        Assert.NotEmpty(errors);
    }

    private static IEnumerable<string> SplitBatches(string script) =>
        System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0);
}
