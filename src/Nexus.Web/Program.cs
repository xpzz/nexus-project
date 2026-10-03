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
builder.Services.AddSingleton(new SettingsProvider(paths));
builder.Services.AddSingleton<INexusDbFactory, SettingsDbFactory>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Nexus.Reconciliation.InventorySnapshotService>();

// Keys live in the data folder (ACL restricted) and are encrypted with DPAPI on Windows,
// so cookies survive app pool recycles and nothing sensitive is stored in clear text.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("AzulNexus")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDirectory));
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddAuthentication(SetupAccessMiddleware.Scheme)
    .AddCookie(SetupAccessMiddleware.Scheme, o =>
    {
        o.Cookie.Name = "AzulNexus.Setup";
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.LoginPath = SetupAccessMiddleware.AccessPath;
    });
builder.Services.AddAuthorization();
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
