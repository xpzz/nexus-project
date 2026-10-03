using Nexus.Data.Support;
using Nexus.Core;
using Nexus.Worker;
using Nexus.Worker.Collection;
using Nexus.Worker.Health;
using Nexus.Worker.Platform;
using Serilog;
using Serilog.Formatting.Compact;

var paths = NexusPaths.Resolve();
var settingsProvider = new SettingsProvider(paths);

var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithProperty("Service", "AzulNexus.Worker")
    .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.LogsDirectory, "worker-.json"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, fileSizeLimitBytes: 50_000_000, rollOnFileSizeLimit: true);
if (OperatingSystem.IsWindows())
{
    logger.WriteTo.EventLog("Azul Nexus", manageEventSource: false, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error);
}

Log.Logger = logger.CreateLogger();

try
{
    if (settingsProvider.Exists)
    {
        var collection = settingsProvider.Current.Collection;
        Log.Information("{Result}", ResourceLimiter.Apply(collection.MaxCpuPercent, collection.MaxMemoryMegabytes));
    }
    else
    {
        Log.Error("Configuração não encontrada em {Path}. O Worker aguarda a configuração.", paths.ConfigFile);
    }

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(o => o.ServiceName = "AzulNexus.Worker");
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(settingsProvider);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<INexusDbFactory, SettingsDbFactory>();
    builder.Services.AddSingleton<IServerLoad, WindowsServerLoad>();
    builder.Services.AddSingleton<ISourceFactory, SourceFactory>();
    builder.Services.AddSingleton<CollectionGate>();
    builder.Services.AddSingleton<JobRunner>();
    builder.Services.AddSingleton<HealthRunner>();
    builder.Services.AddHostedService<CommandProcessor>();
    builder.Services.AddHostedService<Scheduler>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "O serviço AzulNexus.Worker parou por erro inesperado.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
