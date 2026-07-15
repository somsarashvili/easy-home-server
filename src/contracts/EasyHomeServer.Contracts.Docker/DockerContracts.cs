using System.Collections.Immutable;

namespace EasyHomeServer.Contracts.Docker;

/// <summary>
/// The complete set of containers as of one observation, published on the event bus by whichever
/// module manages Docker.
/// </summary>
/// <remarks>
/// <para>
/// This is the type to subscribe to. It carries full state, not a delta, which makes a
/// subscriber's job a reconcile rather than an accumulate: compare what is here against what you
/// have, fix the difference. That is idempotent and self-healing — a subscriber that starts late,
/// restarts, or misses an event converges on the next inventory anyway. Accumulating
/// <see cref="ContainerChanged"/> deltas instead means any single missed event leaves the
/// subscriber permanently wrong with no way to notice.
/// </para>
/// <para>
/// Published on every poll, whether or not anything changed.
/// </para>
/// </remarks>
public sealed record ContainerInventory
{
    /// <summary>When the containers were observed.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Every container the daemon knows about, running or not.</summary>
    public ImmutableArray<ContainerInfo> Containers { get; init; } = [];
}

/// <summary>
/// A container transition, published alongside the inventory.
/// </summary>
/// <remarks>
/// For reacting to a change as an event — logging it, notifying someone. Do not build state from
/// these; see the note on <see cref="ContainerInventory"/>. Derived by diffing observations, so a
/// container that starts and stops between two of them is never reported.
/// </remarks>
public sealed record ContainerChanged
{
    /// <summary>What happened.</summary>
    public required ContainerChangeKind Kind { get; init; }

    /// <summary>The container. For <see cref="ContainerChangeKind.Removed"/>, its last known state.</summary>
    public required ContainerInfo Container { get; init; }

    /// <summary>When it was observed — not necessarily when it happened.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>Kind of container transition.</summary>
public enum ContainerChangeKind
{
    /// <summary>Not seen in the previous observation.</summary>
    Added,

    /// <summary>Absent from the current observation.</summary>
    Removed,

    /// <summary>Same container, different running state.</summary>
    StateChanged,
}

/// <summary>
/// What a subscriber needs to know about a container.
/// </summary>
/// <remarks>
/// Deliberately narrow. This is a published contract between separately versioned packages, so
/// every member here is a commitment; the managing module's own richer model stays private to it.
/// The fields are the ones another module can act on — identity, whether it is running, how to
/// reach it, and what it declares about itself.
/// </remarks>
public sealed record ContainerInfo
{
    /// <summary>Full container id.</summary>
    public required string Id { get; init; }

    /// <summary>Container name, without any leading slash.</summary>
    public required string Name { get; init; }

    /// <summary>Image reference.</summary>
    public required string Image { get; init; }

    /// <summary>True when the container is running now.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>Ports published to the host. Exposed-but-unpublished ports are not included.</summary>
    public ImmutableArray<PublishedPort> Ports { get; init; } = [];

    /// <summary>
    /// The container's own address on the LAN, when it is attached to a network that puts it
    /// there directly — macvlan or ipvlan. Null for the usual case, where the container sits
    /// behind the host and is reached through a published port.
    /// </summary>
    /// <remarks>
    /// A container with one of these is a peer of the host on the network rather than something
    /// behind it: it has its own MAC, answers on its own address, and publishes no ports at all —
    /// <see cref="Ports"/> is empty. Note that the host cannot reach it; that is kernel-level
    /// macvlan isolation, not a misconfiguration.
    /// </remarks>
    public string? LanAddress { get; init; }

    /// <summary>
    /// Ports the image declares with EXPOSE.
    /// </summary>
    /// <remarks>
    /// Normally redundant — what matters is what is published. It is the only thing available for
    /// a container with a <see cref="LanAddress"/>, which publishes nothing and simply listens on
    /// its own address.
    /// </remarks>
    public ImmutableArray<int> ExposedPorts { get; init; } = [];

    /// <summary>True when the container answers on its own LAN address rather than through the host.</summary>
    public bool HasOwnLanAddress => !string.IsNullOrEmpty(LanAddress);

    /// <summary>
    /// Container labels. The extension point of this contract: a module can look for its own
    /// labels here without the Docker module knowing it exists.
    /// </summary>
    public ImmutableDictionary<string, string> Labels { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

/// <summary>A port published from a container to the host.</summary>
public sealed record PublishedPort
{
    /// <summary>Port inside the container.</summary>
    public required int ContainerPort { get; init; }

    /// <summary>Port on the host.</summary>
    public required int HostPort { get; init; }

    /// <summary>Host address the port is bound to; <c>0.0.0.0</c> means every interface.</summary>
    public required string HostIp { get; init; }

    /// <summary>tcp or udp.</summary>
    public required string Protocol { get; init; }

    /// <summary>
    /// True when the port is reachable from other machines. A port bound to loopback is only
    /// reachable on the server itself — advertising it on the network would be a lie.
    /// </summary>
    public bool IsReachableFromNetwork => HostIp is "0.0.0.0" or "::" or "";
}
