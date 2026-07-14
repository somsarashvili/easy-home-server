using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// A complete picture of the daemon at one moment, published on every poll.
/// </summary>
/// <remarks>
/// This type is internal to the module — the page is its only consumer. The per-container
/// transition events below are the ones another module would care about, and those will move to
/// a shared contracts assembly when the Avahi module needs them. See docs/ARCHITECTURE.md on why
/// a cross-module event type cannot simply live here.
/// </remarks>
public sealed record DockerSnapshot
{
    /// <summary>When the poll ran.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Every container, running or not.</summary>
    public ImmutableArray<DockerContainer> Containers { get; init; } = [];

    /// <summary>Every image.</summary>
    public ImmutableArray<DockerImage> Images { get; init; } = [];

    /// <summary>Every volume.</summary>
    public ImmutableArray<DockerVolume> Volumes { get; init; } = [];

    /// <summary>Every network.</summary>
    public ImmutableArray<DockerNetwork> Networks { get; init; } = [];

    /// <summary>Count of containers currently running.</summary>
    public int RunningCount => Containers.Count(c => c.IsRunning);

    /// <summary>Total bytes held by images.</summary>
    public long ImageBytes => Images.Sum(i => i.SizeBytes);
}

/// <summary>
/// A container appeared, disappeared or changed state between two polls.
/// </summary>
/// <remarks>
/// Derived by diffing consecutive polls rather than read from <c>docker events</c>. The
/// consequence is real and worth stating: a container that starts and stops inside one poll
/// interval is never observed. For advertising services on the network — the reason this event
/// exists — settled state is what matters, and a container that flickered was never worth
/// advertising.
/// </remarks>
public sealed record ContainerChanged
{
    /// <summary>What happened.</summary>
    public required ContainerChangeKind Kind { get; init; }

    /// <summary>The container as of the latest poll. For <see cref="ContainerChangeKind.Removed"/>, its last known state.</summary>
    public required DockerContainer Container { get; init; }

    /// <summary>Previous state, for a <see cref="ContainerChangeKind.StateChanged"/>.</summary>
    public ContainerState? PreviousState { get; init; }

    /// <summary>When the change was observed — poll time, not the moment it happened.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>Kind of container transition.</summary>
public enum ContainerChangeKind
{
    /// <summary>A container id not present in the previous poll.</summary>
    Added,

    /// <summary>A container id absent from the current poll.</summary>
    Removed,

    /// <summary>Same container, different state.</summary>
    StateChanged,
}
