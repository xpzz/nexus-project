using Nexus.Data.Entities;
using Nexus.Reconciliation;

namespace Nexus.Tests;

public class ExecutiveSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly EvidencePolicy Policy = EvidencePolicy.From(new Nexus.Core.Configuration.EvidenceSettings());

    private static AssetView View(Action<Asset> set, SourceAvailability? src = null)
    {
        var a = new Asset { Id = Guid.NewGuid(), Name = "AZ-NB-" + Guid.NewGuid().ToString("N")[..4], Platform = "WindowsClient", Ownership = "Corporate", OperatingSystem = "Windows 11" };
        set(a);
        EvidenceEngine.Apply(a, Now, Policy);
        return AssetView.From(a, src ?? SourceAvailability.All);
    }

    private static JobSummary Job(string name, string status = "Concluída", bool success = true, DateTimeOffset? next = null, string? error = null, int? records = 10) =>
        new(name, status, success ? Now.AddMinutes(-5) : null, next ?? Now.AddMinutes(30), records, error);

    private static IReadOnlyList<JobSummary> AllGood() => SourceHealthBuilder.Sources.SelectMany(s => s.Jobs).Select(j => Job(j)).ToList();

    private static InventorySnapshot Snapshot(IReadOnlyList<AssetView> views, IReadOnlyList<JobSummary>? jobs = null, SourceAvailability? src = null) =>
        new(views, [], jobs ?? AllGood(), src ?? SourceAvailability.All, Now, null, 30, Policy);

    private static readonly Action<Asset> Confirmed = a => { a.InSccm = a.InIntune = true; a.SccmClient = true; a.SccmLastSeenAt = Now.AddDays(-1); a.IntuneLastSyncAt = Now.AddDays(-1); a.IntuneChannel = "Mdm"; a.ComplianceState = "compliant"; a.Coverage = "Both"; };

    [Fact]
    public void KpisCountUniqueAssetsAndExcludeDecommissionedFromTheDenominator()
    {
        var views = new[]
        {
            View(Confirmed), View(Confirmed),
            View(a => { a.InIntune = true; a.IntuneLastSyncAt = Now.AddDays(-3); }),
            View(a => { a.InAd = true; a.AdLastLogonAt = Now.AddDays(-3); }),
            View(a => { Confirmed(a); a.NeedsReview = true; }),
            View(a => { a.SccmHealth = "Obsolete"; a.InSccm = true; a.SccmLastSeenAt = Now.AddDays(-100); }),
        };
        var r = Snapshot(views).Executive();

        Assert.Equal(6, r.UniqueAssets);
        Assert.Equal(5, r.Evaluated);
        Assert.Equal(1, r.Decommissioned);
        var k = r.Kpis.ToDictionary(x => x.Key);
        Assert.Equal((2, 5), (k["confirmed"].Count, k["confirmed"].Denominator));
        Assert.Equal(1, k["probable"].Count);
        Assert.Equal(1, k["norecent"].Count);
        Assert.Equal(1, k["conflicts"].Count);
        Assert.Equal(40.0, k["confirmed"].Percent);
        Assert.All(r.Kpis, x => { Assert.StartsWith("/inventario?", x.Link); Assert.False(string.IsNullOrWhiteSpace(x.Definition)); Assert.Contains("5 ativos", x.Population); });
    }

    [Fact]
    public void KpiLinksListExactlyTheAssetsTheyCount()
    {
        var views = new[] { View(Confirmed), View(Confirmed), View(a => { a.InIntune = true; a.IntuneLastSyncAt = Now.AddDays(-3); }), View(a => { Confirmed(a); a.NeedsReview = true; }) };
        var snapshot = Snapshot(views);
        foreach (var kpi in snapshot.Executive().Kpis)
        {
            var q = Nexus.Web.Components.InvQuery.From(new Uri("http://x" + kpi.Link).Query);
            var matched = snapshot.Views.Where(v => v.Asset.OperationalState != OperationalStates.Decommissioned && InventoryQuery.Matches(v, q.ToFilter(), Now)).Count();
            Assert.True(kpi.Count == matched, $"{kpi.Key}: KPI diz {kpi.Count}, a lista tem {matched}");
        }
    }

    [Fact]
    public void MissingCoreSourcesAreReportedAsACaveatOnEveryKpi()
    {
        var jobs = AllGood().Where(j => !j.Name.StartsWith("intune")).ToList();
        var r = Snapshot([View(Confirmed)], jobs).Executive();
        Assert.Contains("Intune", r.MissingSources);
        Assert.All(r.Kpis, k => Assert.Contains("Intune", k.Caveat));
        Assert.Empty(Snapshot([View(Confirmed)]).Executive().MissingSources);
    }

    [Fact]
    public void QueueSeparatesZeroNoDataFailedAndUnavailable()
    {
        var clean = Snapshot([View(Confirmed)]).Executive().Queue.ToDictionary(i => i.Key);
        Assert.Equal(QueueStatus.Zero, clean["nomgmt"].Status);
        Assert.Equal(QueueStatus.Zero, clean["conflict"].Status);
        Assert.True(clean["sources"].Status == QueueStatus.Zero, clean["sources"].Note);
        Assert.Equal(QueueStatus.Unavailable, clean["access"].Status);

        var noMgmt = View(a => { a.InAd = true; a.AdLastLogonAt = Now.AddDays(-2); a.InEntra = true; a.EntraLastSignInAt = Now.AddDays(-2); });
        var with = Snapshot([noMgmt]).Executive().Queue.ToDictionary(i => i.Key);
        Assert.Equal(QueueStatus.HasItems, with["nomgmt"].Status);
        Assert.Equal(1, with["nomgmt"].Count);

        var failing = AllGood().Select(j => j.Name == "sccm.devices" ? Job(j.Name, "Falhou", success: true, error: "sem acesso") : j).ToList();
        Assert.Equal(QueueStatus.Failed, Snapshot([View(Confirmed)], failing).Executive().Queue.Single(i => i.Key == "sources").Status);

        var none = new SourceAvailability(false, false, true, true);
        Assert.Equal(QueueStatus.NoData, Snapshot([View(Confirmed)], AllGood(), none).Executive().Queue.Single(i => i.Key == "nomgmt").Status);
    }

    [Fact]
    public void GovernanceConceptsUseTheirOwnPopulationAndConditionalAccessIsUnavailable()
    {
        var byod = View(a => { a.Ownership = "Personal"; a.Platform = "iOS"; a.OperatingSystem = "iOS"; a.InIntune = true; a.IntuneLastSyncAt = Now.AddDays(-1); a.HasMam = true; a.MamLastSyncAt = Now.AddDays(-1); a.IntuneChannel = "None"; });
        var server = View(a => { a.Platform = "WindowsServer"; a.OperatingSystem = "Windows Server 2022"; a.InSccm = true; a.SccmLastSeenAt = Now.AddDays(-1); });
        var g = Snapshot([View(Confirmed), byod, server]).Executive().Governance.ToDictionary(x => x.Concept);

        Assert.Equal(2, g["Registrado no Entra"].Denominator); // the server is outside the population
        Assert.Equal(1, g["Protegido por MAM"].Denominator); // BYOD only
        Assert.Equal(1, g["Protegido por MAM"].Count);
        Assert.NotNull(g["Acesso permitido por Acesso Condicional"].Unavailable);
        Assert.Null(g["Acesso permitido por Acesso Condicional"].Percent);
    }

    [Fact]
    public void SourceHealthCoversOkLateFailedNeverAndHidesUnconfiguredOptionals()
    {
        var jobs = new List<JobSummary>
        {
            Job("sccm.devices"),
            Job("ad.computers", next: Now.AddHours(-2)),
            Job("intune.devices", "Falhou", error: "403"),
            Job("intune.policies"), Job("intune.mam"),
            Job("entra.devices", status: "—", success: false),
            Job("xdr.endpoints", "Não configurada", success: false),
        };
        var h = SourceHealthBuilder.Build(jobs, Now).ToDictionary(x => x.Key);
        Assert.Equal(SourceHealth.Ok, h["sccm"].Status);
        Assert.Equal(SourceHealth.Late, h["ad"].Status);
        Assert.Equal(SourceHealth.Failed, h["intune"].Status);
        Assert.Equal(SourceHealth.Never, h["entra"].Status);
        Assert.DoesNotContain("xdr", h.Keys);
        Assert.DoesNotContain("netskope", h.Keys);
        Assert.Equal(10, h["sccm"].Records); // device collection only
    }
}

public class InvQueryTests
{
    [Fact]
    public void KeepsOnlyKnownKeysAndRoundTrips()
    {
        var q = Nexus.Web.Components.InvQuery.From("?oper=ConfirmedActive&tipo=notebook&x=1&ordem=nome&cols=equip,tipo");
        Assert.False(q.Has("x"));
        Assert.Equal("ConfirmedActive", q.Get("oper"));
        Assert.Equal(q.QueryString, Nexus.Web.Components.InvQuery.From(q.QueryString).QueryString);
    }

    [Fact]
    public void ChangingAFilterReturnsToTheFirstPageButPagingKeepsIt()
    {
        var q = Nexus.Web.Components.InvQuery.From("?pagina=3&tipo=server");
        Assert.Equal(3, q.PageIndex);
        Assert.False(q.With("oper", "Inactive").Has("pagina"));
        Assert.Equal(4, q.With("pagina", "4").PageIndex);
        Assert.Equal("server", q.With("pagina", "4").Get("tipo"));
    }

    [Fact]
    public void WithoutAndOnlyViewKeysDropFiltersButKeepSortAndColumns()
    {
        var q = Nexus.Web.Components.InvQuery.From("?tipo=server&ordem=nome&desc=true&cols=equip");
        Assert.Equal("/inventario?cols=equip&desc=true&ordem=nome", q.Without("tipo").Url());
        Assert.Equal(q.Without("tipo").Url(), q.OnlyViewKeys().Url());
        Assert.Equal("/inventario", Nexus.Web.Components.InvQuery.From("").Url());
    }

    [Fact]
    public void PageSizeIsClamped()
    {
        Assert.Equal(200, Nexus.Web.Components.InvQuery.From("?tam=99999").PageSize);
        Assert.Equal(10, Nexus.Web.Components.InvQuery.From("?tam=1").PageSize);
        Assert.Equal(25, Nexus.Web.Components.InvQuery.From("?tam=abc").PageSize);
    }

    [Fact]
    public void FlagsFilterRequiresEveryConditionAndIgnoresUnknownOnes()
    {
        var noSerial = new AssetView(new Asset { Name = "A" }, Groups.Computers, Management.None, States.Healthy, 100, [], true);
        var withSerial = new AssetView(new Asset { Name = "B", Serial = "S1" }, Groups.Computers, Management.None, States.Healthy, 100, [], true);
        var now = DateTimeOffset.UtcNow;
        Assert.True(InventoryQuery.Matches(noSerial, new InventoryFilter(Flags: "semserial"), now));
        Assert.False(InventoryQuery.Matches(withSerial, new InventoryFilter(Flags: "semserial"), now));
        Assert.False(InventoryQuery.Matches(noSerial, new InventoryFilter(Flags: "semserial,mdm"), now));
        Assert.False(InventoryQuery.Matches(noSerial, new InventoryFilter(Flags: "inexistente"), now));
    }
}

public class AccessAndSecurityTests
{
    private static System.Security.Claims.ClaimsPrincipal User(params string[] roles) =>
        new(new System.Security.Claims.ClaimsIdentity(roles.Select(r => new System.Security.Claims.Claim("roles", r)).Append(new System.Security.Claims.Claim("preferred_username", "ana@azul.corp")), "test"));

    [Fact]
    public void AnonymousVisitorsOnlyRead()
    {
        var a = Nexus.Web.Setup.CurrentAccess.From(null, "10.0.0.5");
        Assert.True(a.CanRead);
        Assert.False(a.CanAnalyze || a.CanExport || a.CanOperate || a.CanAudit || a.CanSeePersonalData);
        Assert.Equal("visitante@10.0.0.5", a.Actor);
    }

    [Theory]
    [InlineData("Nexus.Leitura", false, false, false, false)]
    [InlineData("Nexus.Analista", true, false, false, true)]
    [InlineData("Nexus.AdminIntegracao", true, true, false, true)]
    [InlineData("Nexus.Auditoria", false, false, true, true)]
    public void RolesGrantOnlyWhatTheyShould(string role, bool analyze, bool operate, bool audit, bool personal)
    {
        var a = Nexus.Web.Setup.CurrentAccess.From(User(role), null);
        Assert.Equal((analyze, operate, audit, personal), (a.CanAnalyze, a.CanOperate, a.CanAudit, a.CanSeePersonalData));
        Assert.Equal(analyze, a.CanExport);
        Assert.Equal("ana@azul.corp", a.Actor);
    }

    [Fact]
    public void TheSetupPrincipalHoldsEveryRole()
    {
        var a = Nexus.Web.Setup.CurrentAccess.From(User(Nexus.Web.Setup.SetupAccessMiddleware.SetupRole), null);
        Assert.True(a.CanAnalyze && a.CanOperate && a.CanAudit && a.CanExport);
    }

    [Theory]
    [InlineData("ana.silva@azul.com", "a•", "@azul.com")]
    [InlineData("jo", "j•••", "")]
    public void PersonalDataIsMaskedButStillDistinguishable(string input, string startsWith, string endsWith)
    {
        var masked = Nexus.Web.Setup.PersonalData.Mask(input);
        Assert.StartsWith(startsWith, masked);
        Assert.EndsWith(endsWith, masked);
        Assert.DoesNotContain("silva", masked);
        Assert.Equal("—", Nexus.Web.Setup.PersonalData.Mask(null));
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("conn Server=x;Password=Sup3rSecret!;Trusted=1", "Sup3rSecret")]
    [InlineData("token=eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk", "eyJhbGci")]
    [InlineData("client_secret: \"abc123\"", "abc123")]
    [InlineData("api-key = XYZ-999", "XYZ-999")]
    public void LogLinesLoseSecrets(string line, string secret)
    {
        var clean = Nexus.Web.Setup.LogTail.Redact(line);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains("***", clean);
    }

    [Fact]
    public void OrdinaryLogLinesAreUntouched() =>
        Assert.Equal("Coleta sccm.devices concluída: 474 registros", Nexus.Web.Setup.LogTail.Redact("Coleta sccm.devices concluída: 474 registros"));

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "'=")]
    [InlineData("+1+1", "'+")]
    [InlineData("@SUM(A1)", "'@")]
    [InlineData("normal", "\"normal\"")]
    public void CsvCellsNeutralizeFormulas(string value, string expectedFragment) =>
        Assert.Contains(expectedFragment, Nexus.Web.Setup.InventoryCsv.Cell(value));

    [Fact]
    public void CsvEscapesQuotesAndNewlines() =>
        Assert.Equal("\"a \"\"b\"\" c d\"", Nexus.Web.Setup.InventoryCsv.Cell("a \"b\" c\nd"));
}
