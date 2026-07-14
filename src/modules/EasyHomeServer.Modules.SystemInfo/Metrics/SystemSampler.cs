using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.SystemInfo.Metrics;

/// <summary>
/// Samples the machine on a fixed interval and publishes each snapshot on the event bus.
/// </summary>
/// <remarks>
/// <para>
/// One sampler serves every connected browser. Sampling per-page would multiply /proc reads by
/// the number of open tabs and, worse, give each tab a different CPU delta window — the same
/// machine would show different numbers in two windows.
/// </para>
/// <para>
/// Sampling continues with no subscribers so that the history ring is already populated when a
/// page opens, and so a reconnecting browser sees a filled sparkline rather than an empty one.
/// </para>
/// </remarks>
public sealed class SystemSampler : ModuleBackgroundService
{
    private readonly ProcReader _reader;
    private readonly MetricsHistory _history;
    private readonly IEventBus _eventBus;
    private readonly TimeSpan _interval;

    /// <summary>The machine's static identity, read once at construction.</summary>
    public SystemIdentity Identity { get; }

    /// <summary>True when this platform exposes procfs and sampling can do anything useful.</summary>
    public bool IsSupported => ProcReader.IsSupported;

    public SystemSampler(
        ProcReader reader,
        MetricsHistory history,
        IEventBus eventBus,
        SystemInfoOptions options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _reader = reader;
        _history = history;
        _eventBus = eventBus;
        _interval = TimeSpan.FromSeconds(options.SampleIntervalSeconds);
        Identity = reader.ReadIdentity();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsSupported)
        {
            // Developer machines are not Linux. Say so once and stop, rather than logging a
            // failed read every two seconds forever.
            Logger.LogWarning(
                "procfs is not available on this platform ({Platform}); system metrics will not be sampled.",
                Environment.OSVersion.Platform);

            return;
        }

        Logger.LogInformation("Sampling system metrics every {IntervalSeconds:0.#}s.", _interval.TotalSeconds);

        // PeriodicTimer paces on a fixed schedule rather than interval-after-work, so a slow
        // read cannot make the effective sampling period drift.
        using var timer = new PeriodicTimer(_interval);

        // Prime the counter baselines immediately; the first tick then has a real delta to
        // report instead of wasting the first interval.
        Sample();

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var snapshot = Sample();

            if (snapshot is not null)
            {
                await _eventBus.PublishAsync(snapshot, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private SystemSnapshot? Sample()
    {
        try
        {
            var snapshot = _reader.Read();
            _history.Add(snapshot);

            return snapshot;
        }
        catch (Exception ex)
        {
            // A parse failure must not end the sampling loop: the next tick may well succeed,
            // and a dead sampler means a permanently frozen page.
            Logger.LogError(ex, "Failed to sample system metrics; will retry on the next tick.");

            return null;
        }
    }
}
