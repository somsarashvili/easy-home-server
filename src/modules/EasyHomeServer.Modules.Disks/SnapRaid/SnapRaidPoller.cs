using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.SnapRaid;

/// <summary>
/// Re-reads the SnapRAID array on a slow interval and publishes what it finds.
/// </summary>
/// <remarks>
/// Separate from <see cref="Disks.DiskPoller"/> rather than folded into it, because the two have
/// nothing in common but the page they feed. Reading block devices is cheap and wanted often;
/// reading the array is expensive, contends with the sync that cron runs hourly, and reports
/// numbers that change on that same hourly rhythm.
/// </remarks>
public sealed class SnapRaidPoller : ModuleBackgroundService
{
    private readonly SnapRaidCli _cli;
    private readonly IEventBus _eventBus;
    private readonly TimeSpan _interval;

    /// <summary>The most recent reading, or null before the first one.</summary>
    public SnapRaidReading? Latest { get; private set; }

    /// <summary>
    /// The last reading that actually produced a report.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Latest"/> so a read landing mid-sync does not blank the page.
    /// A busy answer means the numbers are momentarily unavailable, not that they changed.
    /// </remarks>
    public SnapRaidStatus? LastGoodStatus { get; private set; }

    public SnapRaidPoller(
        SnapRaidCli cli,
        IEventBus eventBus,
        DisksOptions options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _cli = cli;
        _eventBus = eventBus;
        _interval = TimeSpan.FromSeconds(options.SnapRaidPollIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await _cli.IsInstalledAsync(stoppingToken).ConfigureAwait(false))
        {
            Latest = new SnapRaidReading { Outcome = SnapRaidOutcome.NotInstalled };

            Logger.LogInformation("snapraid is not installed; the parity tab will say so.");

            return;
        }

        Logger.LogInformation("Reading the SnapRAID array every {IntervalMinutes:0.#} minutes.",
            _interval.TotalMinutes);

        using var timer = new PeriodicTimer(_interval);

        await PollAsync(stoppingToken).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Re-reads now, for the refresh button.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) => PollAsync(cancellationToken);

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reading = await _cli.ReadAsync(cancellationToken).ConfigureAwait(false);

            Latest = reading;

            if (reading.Status is not null)
            {
                LastGoodStatus = reading.Status;
            }

            await _eventBus.PublishAsync(reading, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not read the SnapRAID array; will retry on the next tick.");
        }
    }
}
