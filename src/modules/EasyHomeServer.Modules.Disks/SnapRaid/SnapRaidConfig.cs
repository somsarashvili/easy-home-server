using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Disks.SnapRaid;

/// <summary>One data disk in the array, as <c>snapraid.conf</c> names it.</summary>
public sealed record SnapRaidDataDisk
{
    /// <summary>SnapRAID's own name for it, e.g. <c>d1</c>. Used in every report it prints.</summary>
    public required string Name { get; init; }

    /// <summary>Where it is mounted, e.g. <c>/mnt/data1</c>.</summary>
    public required string Path { get; init; }
}

/// <summary>
/// The array as <c>snapraid.conf</c> declares it.
/// </summary>
/// <remarks>
/// The config is what SnapRAID protects, which is not the same as what mergerfs pools. Nothing
/// keeps the two in step: a disk added to the pool but not to snapraid.conf is served happily and
/// protected by nothing at all, which is the mistake this pairing invites.
/// </remarks>
public sealed record SnapRaidConfig
{
    /// <summary>Parity files, one per parity level. Their count is how many disks may fail.</summary>
    public ImmutableArray<string> ParityFiles { get; init; } = [];

    /// <summary>Content files: SnapRAID's index of what it protects.</summary>
    public ImmutableArray<string> ContentFiles { get; init; } = [];

    /// <summary>The data disks under protection.</summary>
    public ImmutableArray<SnapRaidDataDisk> DataDisks { get; init; } = [];

    /// <summary>Patterns excluded from protection.</summary>
    public ImmutableArray<string> Excludes { get; init; } = [];

    /// <summary>How many disks can fail without losing data: one per parity file.</summary>
    public int ParityCount => ParityFiles.Length;
}
