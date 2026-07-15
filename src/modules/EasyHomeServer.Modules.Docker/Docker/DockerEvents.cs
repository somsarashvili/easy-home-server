using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// A complete picture of the daemon at one moment, published on every poll and consumed by this
/// module's own page.
/// </summary>
/// <remarks>
/// Module-internal on purpose. It carries the module's rich models — images, volumes, networks,
/// health, exit codes — which are this module's business and not something other modules should
/// be coupled to. The cross-module view is the much narrower
/// <see cref="EasyHomeServer.Contracts.Docker.ContainerInventory"/>, published alongside it and
/// living in its own shared assembly. See docs/ARCHITECTURE.md.
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

    /// <summary>Compose projects, merged from container labels and the stacks directory.</summary>
    public ImmutableArray<ComposeProject> Projects { get; init; } = [];

    /// <summary>Count of containers currently running.</summary>
    public int RunningCount => Containers.Count(c => c.IsRunning);

    /// <summary>Total bytes held by images.</summary>
    public long ImageBytes => Images.Sum(i => i.SizeBytes);
}
