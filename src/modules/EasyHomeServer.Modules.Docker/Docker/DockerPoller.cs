using System.Collections.Immutable;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// Polls the daemon and publishes a <see cref="DockerSnapshot"/>, plus a
/// <see cref="ContainerChanged"/> for each transition since the previous poll.
/// </summary>
/// <remarks>
/// One poller serves every browser, for the same reason SystemInfo has one sampler: polling per
/// page would multiply <c>docker inspect</c> calls by the number of open tabs.
/// </remarks>
public sealed class DockerPoller : ModuleBackgroundService
{
    private readonly DockerCli _cli;
    private readonly IEventBus _eventBus;
    private readonly TimeSpan _interval;

    private ImmutableDictionary<string, DockerContainer> _previousContainers =
        ImmutableDictionary<string, DockerContainer>.Empty;

    /// <summary>The most recent snapshot, or null before the first poll completes.</summary>
    public DockerSnapshot? Latest { get; private set; }

    /// <summary>Whether the daemon is reachable, refreshed on every poll.</summary>
    public DockerCli.Availability Availability { get; private set; } =
        new() { IsAvailable = false, Reason = "Not checked yet." };

    public DockerPoller(DockerCli cli, IEventBus eventBus, DockerOptions options, ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _cli = cli;
        _eventBus = eventBus;
        _interval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Polling Docker every {IntervalSeconds:0.#}s.", _interval.TotalSeconds);

        using var timer = new PeriodicTimer(_interval);

        await PollAsync(stoppingToken).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            var availability = await _cli.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var wasAvailable = Availability.IsAvailable;
            Availability = availability;

            if (!availability.IsAvailable)
            {
                if (wasAvailable)
                {
                    Logger.LogWarning("Docker became unavailable: {Reason}", availability.Reason);
                }

                // Keep the last snapshot rather than blanking the page: a daemon restart is
                // brief, and stale-but-labelled beats empty.
                return;
            }

            if (!wasAvailable)
            {
                Logger.LogInformation("Docker is available (server {Version}).", availability.Version);
            }

            var containers = await _cli.ListContainersAsync(cancellationToken).ConfigureAwait(false);
            var images = await _cli.ListImagesAsync(cancellationToken).ConfigureAwait(false);
            var volumes = await _cli.ListVolumesAsync(cancellationToken).ConfigureAwait(false);
            var networks = await _cli.ListNetworksAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = new DockerSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Containers = containers,
                Images = WithUsageCounts(images, containers),
                Volumes = WithUsageCounts(volumes, containers),
                Networks = WithUsageCounts(networks, containers),
            };

            Latest = snapshot;

            await _eventBus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await PublishTransitionsAsync(containers, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let one bad poll end the loop; the next one may well succeed.
            Logger.LogError(ex, "A Docker poll failed; will retry on the next tick.");
        }
    }

    private async Task PublishTransitionsAsync(
        ImmutableArray<DockerContainer> containers,
        CancellationToken cancellationToken)
    {
        var current = containers.ToImmutableDictionary(c => c.Id, StringComparer.Ordinal);
        var previous = _previousContainers;
        _previousContainers = current;

        // The first poll is a baseline, not a burst of "everything just started".
        if (previous.IsEmpty)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var changes = new List<ContainerChanged>();

        foreach (var (id, container) in current)
        {
            if (!previous.TryGetValue(id, out var before))
            {
                changes.Add(new ContainerChanged
                {
                    Kind = ContainerChangeKind.Added,
                    Container = container,
                    ObservedAtUtc = now,
                });
            }
            else if (before.State != container.State)
            {
                changes.Add(new ContainerChanged
                {
                    Kind = ContainerChangeKind.StateChanged,
                    Container = container,
                    PreviousState = before.State,
                    ObservedAtUtc = now,
                });
            }
        }

        foreach (var (id, container) in previous)
        {
            if (!current.ContainsKey(id))
            {
                changes.Add(new ContainerChanged
                {
                    Kind = ContainerChangeKind.Removed,
                    Container = container,
                    ObservedAtUtc = now,
                });
            }
        }

        foreach (var change in changes)
        {
            Logger.LogInformation(
                "Container {Name} ({ShortId}): {Kind}{Transition}",
                change.Container.Name,
                change.Container.ShortId,
                change.Kind,
                change.Kind == ContainerChangeKind.StateChanged
                    ? $" {change.PreviousState} -> {change.Container.State}"
                    : string.Empty);

            await _eventBus.PublishAsync(change, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Counts container references so the UI can mark what is safe to remove. Computed here
    /// rather than per render: it is a join across the whole snapshot and does not change
    /// between polls.
    /// </summary>
    private static ImmutableArray<DockerImage> WithUsageCounts(
        ImmutableArray<DockerImage> images,
        ImmutableArray<DockerContainer> containers)
    {
        return [.. images.Select(image => image with
        {
            UsedByContainers = containers.Count(c =>
                image.Tags.Contains(c.Image, StringComparer.Ordinal)
                || string.Equals(c.Image, image.Id, StringComparison.Ordinal)),
        })];
    }

    private static ImmutableArray<DockerVolume> WithUsageCounts(
        ImmutableArray<DockerVolume> volumes,
        ImmutableArray<DockerContainer> containers)
    {
        // Counts every container, not just running ones: a stopped container still owns its
        // volume, and removing it would destroy data the container expects to find on restart.
        return [.. volumes.Select(volume => volume with
        {
            UsedByContainers = containers.Count(c => c.VolumeMounts.Contains(volume.Name, StringComparer.Ordinal)),
        })];
    }

    private static ImmutableArray<DockerNetwork> WithUsageCounts(
        ImmutableArray<DockerNetwork> networks,
        ImmutableArray<DockerContainer> containers)
    {
        return [.. networks.Select(network => network with
        {
            UsedByContainers = containers.Count(c => c.Networks.Contains(network.Name, StringComparer.Ordinal)),
        })];
    }
}
