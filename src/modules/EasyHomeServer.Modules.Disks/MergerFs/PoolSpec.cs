using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Disks.MergerFs;

/// <summary>A pool to create.</summary>
public sealed record PoolSpec
{
    /// <summary>Where the pool will be mounted, e.g. <c>/data/storage</c>.</summary>
    public required string MountPoint { get; init; }

    /// <summary>
    /// The filesystems to pool, in order.
    /// </summary>
    /// <remarks>
    /// Order is part of the configuration, not presentation: <c>ff</c> fills the first branch
    /// before moving on, which is what puts an SSD cache branch first.
    /// </remarks>
    public required ImmutableArray<string> Branches { get; init; }

    /// <summary>The policy choosing a branch for a new file.</summary>
    public string CreatePolicy { get; init; } = "ff";

    /// <summary>Space a branch must keep free to stay eligible for new files.</summary>
    public long MinFreeSpaceBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    /// <summary>Whether to move a file to another branch when its own fills mid-write.</summary>
    public bool MoveOnEnoSpc { get; init; } = true;
}
