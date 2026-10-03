using System.Diagnostics;
using Nexus.Core.Configuration;

namespace Nexus.Collectors.Sccm;

/// <summary>
/// Protects the site database (SPEC §5.3): limits concurrent queries and signals a backoff when
/// a query takes longer than the configured threshold.
/// </summary>
public sealed class SccmQueryGate(SccmSettings settings)
{
    private readonly SemaphoreSlim _semaphore = new(Math.Max(1, settings.MaxConcurrentQueries));
    private int _active;

    public int MaxConcurrency { get; } = Math.Max(1, settings.MaxConcurrentQueries);
    public int PeakConcurrency { get; private set; }
    public bool SlowQueryObserved { get; private set; }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        var active = Interlocked.Increment(ref _active);
        lock (_semaphore)
        {
            PeakConcurrency = Math.Max(PeakConcurrency, active);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await query(cancellationToken);
        }
        finally
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(settings.SlowQueryThresholdSeconds))
            {
                SlowQueryObserved = true;
            }

            Interlocked.Decrement(ref _active);
            _semaphore.Release();
        }
    }
}
