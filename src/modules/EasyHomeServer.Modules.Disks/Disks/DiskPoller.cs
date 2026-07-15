using EasyHomeServer.Modules.Disks.MergerFs;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>
/// Re-reads the block devices on an interval and publishes a <see cref="DiskSnapshot"/>.
/// </summary>
/// <remarks>
/// Polled rather than driven by udev events. Watching udev would need a netlink socket held open
/// from module code — real privileged I/O outside <see cref="ISystemRunner"/> — to learn about
/// something that happens when a human plugs a disk in. Ten seconds is soon enough for that.
/// </remarks>
public sealed class DiskPoller : ModuleBackgroundService
{
    private readonly BlockDeviceReader _reader;
    private readonly MergerFsReader _mergerFsReader;
    private readonly IEventBus _eventBus;
    private readonly TimeSpan _interval;

    /// <summary>The most recent reading, or null before the first one.</summary>
    public DiskSnapshot? Latest { get; private set; }

    /// <summary>Whether lsblk is available. False means the page has nothing to show.</summary>
    public bool IsAvailable { get; private set; } = true;

    public DiskPoller(
        BlockDeviceReader reader,
        MergerFsReader mergerFsReader,
        IEventBus eventBus,
        DisksOptions options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _reader = reader;
        _mergerFsReader = mergerFsReader;
        _eventBus = eventBus;
        _interval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IsAvailable = await _reader.IsAvailableAsync(stoppingToken).ConfigureAwait(false);

        if (!IsAvailable)
        {
            Logger.LogWarning("lsblk is not available; storage cannot be read on this machine.");

            return;
        }

        Logger.LogInformation("Reading block devices every {IntervalSeconds:0.#}s.", _interval.TotalSeconds);

        using var timer = new PeriodicTimer(_interval);

        await PollAsync(stoppingToken).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Re-reads immediately, for after an action changes something.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) => PollAsync(cancellationToken);

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = new DiskSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Devices = devices,
                Pools = _mergerFsReader.Read(),
            };

            Latest = snapshot;

            await _eventBus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not read block devices; will retry on the next tick.");
        }
    }
}
