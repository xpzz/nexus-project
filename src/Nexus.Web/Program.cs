using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nexus.Core;
using Nexus.Data;
using Nexus.Data.Support;
using Nexus.Web.Components;
using Nexus.Web.Setup;
using Serilog;
using Serilog.Formatting.Compact;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new System.Globalization.CultureInfo("pt-BR");
var paths = NexusPaths.Resolve();
var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.WithProperty("Service", "AzulNexus.Web")
    .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.LogsDirectory, "web-.json"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, fileSizeLimitBytes: 50_000_000, rollOnFileSizeLimit: true);
if (OperatingSystem.IsWindows())
{
    logger.WriteTo.EventLog("Azul Nexus", manageEventSource: false, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error);
}

Log.Logger = logger.CreateLogger();

// Hosting: "service" = Windows service with Kestrel (needs only "log on as a service"); "iis" = IIS in-process.
var settingsStore = new Nexus.Core.Configuration.SettingsStore(paths);
var configuredSettings = settingsStore.Load();
var serviceHosting = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
    || string.Equals(Environment.GetEnvironmentVariable("NEXUS_HOSTING"), "service", StringComparison.OrdinalIgnoreCase);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
if (serviceHosting)
{
    builder.Host.UseWindowsService(o => o.ServiceName = "AzulNexus.Web");
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
        try
        {
            certificate = Nexus.Web.Hosting.HttpsCertificateLoader.Load(configuredSettings.Web.CertificateThumbprint);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{Message}", ex.Message);
            Log.CloseAndFlush();
            throw;
        }

        kestrel.ListenAnyIP(configuredSettings.Web.HttpsPort, listen => listen.UseHttps(certificate));
    });
}

builder.Services.AddSerilog();
builder.Services.AddSingleton(paths);
var settingsProvider = new SettingsProvider(paths);
builder.Services.AddSingleton(settingsProvider);
builder.Services.AddSingleton<INexusDbFactory, SettingsDbFactory>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new Nexus.Reconciliation.InventorySnapshotService(sp.GetRequiredService<INexusDbFactory>(), sp.GetRequiredService<TimeProvider>(),
    () => sp.GetRequiredService<SettingsProvider>().Exists ? Nexus.Reconciliation.EvidencePolicy.From(sp.GetRequiredService<SettingsProvider>().Current.Evidence) : Nexus.Reconciliation.EvidencePolicy.From(new Nexus.Core.Configuration.EvidenceSettings()),
    () =>
    {
        var g = sp.GetRequiredService<SettingsProvider>().Exists ? sp.GetRequiredService<SettingsProvider>().Current.Governance : new Nexus.Core.Configuration.GovernanceSettings();
        return new Nexus.Reconciliation.GovernanceSettingsView(Math.Clamp(g.UrlBlocklistLimit, 1, 100_000), g.UrlBlocklistReservePercent, 180);
    }));
builder.Services.AddHostedService<Nexus.Web.Hosting.SnapshotWarmup>();

// Keys live in the data folder (ACL restricted) and are encrypted with DPAPI on Windows,
// so cookies survive app pool recycles and nothing sensitive is stored in clear text.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("AzulNexus")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDirectory));
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

var authSettings = settingsProvider.Exists ? settingsProvider.Current.Web : null;
var entraPrepared = authSettings is { UsesEntra: true } ? EntraSignIn.Prepare(Nexus.Core.Configuration.AzureSettingsStore.Load(paths)) : (null, null);
var entraProblem = entraPrepared.Item2;
var authBuilder = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = entraPrepared.Item1 is null ? SetupAccessMiddleware.Scheme : EntraSignIn.PolicyScheme;
    if (entraPrepared.Item1 is not null)
    {
        o.DefaultChallengeScheme = EntraSignIn.ChallengeScheme;
    }
})
    .AddCookie(SetupAccessMiddleware.Scheme, o =>
    {
        o.Cookie.Name = "AzulNexus.Setup";
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.LoginPath = SetupAccessMiddleware.AccessPath;
    });
if (entraPrepared.Item1 is { } entra)
{
    EntraSignIn.Register(authBuilder, entra);
}

builder.Services.AddSingleton(new EntraStatus(entraPrepared.Item1 is not null, entraProblem));
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp => { var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext; return Nexus.Web.Setup.CurrentAccess.From(http?.User, http?.Connection.RemoteIpAddress?.ToString()); });
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/erro", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<SetupAccessMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

// Liveness for Install-AzulNexus.ps1 and monitoring. No sensitive data.
app.MapGet("/healthz", async (SettingsProvider settings, INexusDbFactory dbFactory, CancellationToken ct) =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "";
    if (!settings.Exists)
    {
        return Results.Json(new { status = "unconfigured", version }, statusCode: 503);
    }

    try
    {
        await using var db = dbFactory.Create();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
        var setup = await SetupModeService.GetAsync(db, ct);
        return Results.Json(new { status = pending == 0 ? "ok" : "migrations-pending", version, setupMode = setup.SetupModeActive });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "database-unreachable", version }, statusCode: 503);
    }
});

// Asks the Worker to read the installed software of one device. Read-only on the sources; throttled to one pending request per device.
app.MapPost("/dispositivo/{id:guid}/inventario", async (Guid id, INexusDbFactory dbFactory, TimeProvider clock, CancellationToken ct) =>
{
    await using var db = dbFactory.Create();
    if (!await db.Assets.AnyAsync(a => a.Id == id, ct))
    {
        return Results.NotFound();
    }

    var now = clock.GetUtcNow();
    var fetch = await db.InventoryFetches.FirstOrDefaultAsync(f => f.AssetId == id, ct);
    if (fetch is not { Status: "Pending" } || now - fetch.RequestedAt > TimeSpan.FromMinutes(2))
    {
        if (fetch is null)
        {
            fetch = new Nexus.Data.Entities.InventoryFetch { AssetId = id };
            db.InventoryFetches.Add(fetch);
        }

        fetch.RequestedAt = now;
        fetch.Status = "Pending";
        fetch.Message = null;
        await db.SaveChangesAsync(ct);
        await CommandQueue.EnqueueAsync(db, Nexus.Data.Entities.CommandTypes.FetchInventory, id.ToString(), "web:visitante", ct);
    }

    return Results.Redirect($"/dispositivo/{id}#software");
}).DisableAntiforgery();

app.MapInventoryEndpoints();

app.MapGet("/diagnostico", async (HttpContext context, SettingsProvider settings, CancellationToken ct) =>
{
    var target = Path.Combine(Path.GetTempPath(), $"azulnexus-diagnostico-{Guid.NewGuid():N}.zip");
    await Diagnostics.CreateAsync(paths, new Nexus.Core.Configuration.SettingsStore(paths), target, ct);
    var bytes = await File.ReadAllBytesAsync(target, ct);
    File.Delete(target);
    await using (var db = context.RequestServices.GetRequiredService<INexusDbFactory>().Create())
    {
        Audit.Record(db, context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "local", "diagnostics.downloaded");
        await db.SaveChangesAsync(ct);
    }

    return Results.File(bytes, "application/zip", $"azulnexus-diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
