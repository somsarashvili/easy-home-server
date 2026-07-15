using System.Collections.Immutable;
using EasyHomeServer.Contracts.Docker;
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
    private readonly ComposeDiscovery _compose;
    private readonly IEventBus _eventBus;
    private readonly TimeSpan _interval;

    private ImmutableDictionary<string, DockerContainer> _previousContainers =
        ImmutableDictionary<string, DockerContainer>.Empty;

    /// <summary>The most recent snapshot, or null before the first poll completes.</summary>
    public DockerSnapshot? Latest { get; private set; }

    /// <summary>Whether the daemon is reachable, refreshed on every poll.</summary>
    public DockerCli.Availability Availability { get; private set; } =
        new() { IsAvailable = false, Reason = "Not checked yet." };

    public DockerPoller(
        DockerCli cli,
        ComposeDiscovery compose,
        IEventBus eventBus,
        DockerOptions options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _cli = cli;
        _compose = compose;
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

            // Needs both lists: whether a container has its own address on the LAN depends on the
            // *network's* driver, which the container's own inspect output does not carry.
            containers = WithLanAddresses(containers, networks);

            var snapshot = new DockerSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Containers = containers,
                Images = WithUsageCounts(images, containers),
                Volumes = WithUsageCounts(volumes, containers),
                Networks = WithUsageCounts(networks, containers),

                // Derived from the containers just read plus a directory scan; no extra docker
                // calls, so it costs nothing beyond the scan.
                Projects = _compose.Discover(containers),
            };

            Latest = snapshot;

            // Internal snapshot for this module's own page…
            await _eventBus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);

            // …and the narrow, shared contract for everyone else. Published every poll even when
            // nothing changed: it is the authoritative state subscribers reconcile against, so a
            // subscriber that starts late or misses a delta still converges.
            await _eventBus
                .PublishAsync(
                    new ContainerInventory
                    {
                        ObservedAtUtc = snapshot.TimestampUtc,
                        Containers = [.. containers.Select(ToContractInfo)],
                    },
                    cancellationToken)
                .ConfigureAwait(false);

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
        var changes = new List<(ContainerChangeKind Kind, DockerContainer Container, ContainerState? Previous)>();

        foreach (var (id, container) in current)
        {
            if (!previous.TryGetValue(id, out var before))
            {
                changes.Add((ContainerChangeKind.Added, container, null));
            }
            else if (before.State != container.State)
            {
                changes.Add((ContainerChangeKind.StateChanged, container, before.State));
            }
        }

        foreach (var (id, container) in previous)
        {
            if (!current.ContainsKey(id))
            {
                changes.Add((ContainerChangeKind.Removed, container, null));
            }
        }

        foreach (var (kind, container, previousState) in changes)
        {
            Logger.LogInformation(
                "Container {Name} ({ShortId}): {Kind}{Transition}",
                container.Name,
                container.ShortId,
                kind,
                kind == ContainerChangeKind.StateChanged ? $" {previousState} -> {container.State}" : string.Empty);

            await _eventBus
                .PublishAsync(
                    new ContainerChanged
                    {
                        Kind = kind,
                        Container = ToContractInfo(container),
                        ObservedAtUtc = now,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drivers that put a container directly on the physical network with its own MAC and
    /// address, rather than behind the host.
    /// </summary>
    private static readonly string[] LanAddressedDrivers = ["macvlan", "ipvlan"];

    /// <summary>
    /// Fills in <see cref="DockerContainer.LanAddress"/> for containers attached to a macvlan or
    /// ipvlan network.
    /// </summary>
    /// <remarks>
    /// Such a container publishes no ports — it does not need to, since it answers on its own
    /// address — so everything keyed off published ports treats it as having nothing to offer.
    /// Recording the address is what lets the rest of the system know it is reachable at all.
    /// </remarks>
    private static ImmutableArray<DockerContainer> WithLanAddresses(
        ImmutableArray<DockerContainer> containers,
        ImmutableArray<DockerNetwork> networks)
    {
        var lanNetworks = networks
            .Where(n => LanAddressedDrivers.Contains(n.Driver, StringComparer.OrdinalIgnoreCase))
            .Select(n => n.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (lanNetworks.Count == 0)
        {
            return containers;
        }

        return
        [
            .. containers.Select(container =>
            {
                var attachment = container.NetworkAttachments
                    .FirstOrDefault(a => lanNetworks.Contains(a.NetworkName) && a.IpAddress.Length > 0);

                return attachment is null ? container : container with { LanAddress = attachment.IpAddress };
            }),
        ];
    }

    /// <summary>
    /// Narrows the module's rich model down to the published contract. Everything not crossing
    /// the module boundary — health, exit codes, restart policy, networks — stops here.
    /// </summary>
    private static ContainerInfo ToContractInfo(DockerContainer container) => new()
    {
        Id = container.Id,
        Name = container.Name,
        Image = container.Image,
        IsRunning = container.IsRunning,
        Ports = [.. container.Ports.Select(p => new PublishedPort
        {
            ContainerPort = p.ContainerPort,
            HostPort = p.HostPort,
            HostIp = p.HostIp,
            Protocol = p.Protocol,
        })],
        LanAddress = container.LanAddress,
        ExposedPorts = container.ExposedPorts,
        Labels = container.Labels,
    };

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
