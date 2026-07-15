using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Disks.MergerFs;

/// <summary>How a branch may be written to, as mergerfs reports it in its branch list.</summary>
public enum BranchMode
{
    /// <summary>Read and write. The usual case.</summary>
    ReadWrite,

    /// <summary>Read only. Never chosen for a new file.</summary>
    ReadOnly,

    /// <summary>Readable and writable, but never chosen for a *new* file ("no create").</summary>
    NoCreate,

    /// <summary>Something this module does not recognise.</summary>
    Unknown,
}

/// <summary>One underlying filesystem in a pool.</summary>
public sealed record PoolBranch
{
    /// <summary>Where the branch itself is mounted, e.g. <c>/mnt/data1</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Whether mergerfs may write to it, and whether it may create there.</summary>
    public required BranchMode Mode { get; init; }

    /// <summary>Size of the filesystem behind it, or null when it could not be read.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Free space on it, or null when it could not be read.</summary>
    public long? AvailableBytes { get; init; }

    /// <summary>Space in use, when both size and free space are known.</summary>
    public long? UsedBytes => TotalBytes - AvailableBytes;

    /// <summary>Fraction of the branch in use, 0 to 1, or null when it could not be read.</summary>
    public double? UsedFraction =>
        TotalBytes is > 0 && UsedBytes is { } used ? (double)used / TotalBytes.Value : null;

    /// <summary>Whether the branch is present and readable right now.</summary>
    public bool IsPresent => TotalBytes is not null;
}

/// <summary>
/// A mergerfs pool: several filesystems presented as one directory tree.
/// </summary>
/// <remarks>
/// The fields here come from the running mergerfs rather than from whatever config file created
/// it, because the two disagree. A unit saying <c>moveonenospc=true</c> produces a mergerfs whose
/// answer is <c>mfs</c> — <c>true</c> is an alias for a policy, and only the process knows which.
/// </remarks>
public sealed record MergerFsPool
{
    /// <summary>Where the pool is mounted, e.g. <c>/data/storage</c>.</summary>
    public required string MountPoint { get; init; }

    /// <summary>The filesystems behind it, in mergerfs's own order — which is what <c>ff</c> follows.</summary>
    public ImmutableArray<PoolBranch> Branches { get; init; } = [];

    /// <summary>
    /// The policy picking a branch for a new file, e.g. <c>ff</c> (first found) or <c>mfs</c>
    /// (most free space). Null when it could not be read.
    /// </summary>
    public string? CreatePolicy { get; init; }

    /// <summary>
    /// Space a branch must keep free to stay eligible for new files. Null when it could not be read.
    /// </summary>
    public long? MinFreeSpaceBytes { get; init; }

    /// <summary>
    /// The policy for moving a file when its branch fills mid-write, or null when it is off.
    /// </summary>
    public string? MoveOnEnoSpc { get; init; }

    /// <summary>
    /// The mergerfs version, when it admits to one. Debian's build reports "unknown", so this is
    /// shown only when it says something useful.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>Whether the running mergerfs answered, rather than the details being guessed.</summary>
    public required bool ConfigReadFromRuntime { get; init; }

    /// <summary>Combined size of every readable branch.</summary>
    public long TotalBytes => Branches.Sum(b => b.TotalBytes ?? 0);

    /// <summary>Combined free space of every readable branch.</summary>
    public long AvailableBytes => Branches.Sum(b => b.AvailableBytes ?? 0);

    /// <summary>Combined space in use.</summary>
    public long UsedBytes => TotalBytes - AvailableBytes;

    /// <summary>Fraction of the pool in use, 0 to 1.</summary>
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;

    /// <summary>Branches listed by mergerfs that could not be read — a disk that did not come back.</summary>
    public IEnumerable<PoolBranch> MissingBranches => Branches.Where(b => !b.IsPresent);

    /// <summary>
    /// The branches a create policy is allowed to choose from.
    /// </summary>
    /// <remarks>
    /// <c>minfreespace</c> is a filter applied before the policy runs, not a warning threshold: a
    /// branch below it is not considered at all.
    /// </remarks>
    public IEnumerable<PoolBranch> EligibleBranches =>
        Branches.Where(branch => branch.IsPresent
                                 && branch.Mode == BranchMode.ReadWrite
                                 && (MinFreeSpaceBytes is not { } minimum
                                     || branch.AvailableBytes >= minimum));

    /// <summary>
    /// Whether the pool can take a new file at all.
    /// </summary>
    /// <remarks>
    /// False is worth shouting about, because it does not look like a full disk. When every branch
    /// is below <c>minfreespace</c>, mergerfs fails creates with ENOSPC while <c>df</c> still
    /// reports the leftover space as free — so the pool reads as healthy and refuses every write.
    /// </remarks>
    public bool AcceptsNewFiles => EligibleBranches.Any();

    /// <summary>
    /// The branch the next new file would land on, or null when that cannot be known.
    /// </summary>
    /// <remarks>
    /// Only the policies whose choice depends on nothing but free space can be answered here.
    /// <c>epmfs</c> and friends pick by which branch already holds the parent directory, so the
    /// answer differs per path; claiming one would be a guess dressed as a fact.
    /// </remarks>
    public PoolBranch? NextWriteTarget => CreatePolicy?.ToLowerInvariant() switch
    {
        "ff" => EligibleBranches.FirstOrDefault(),
        "mfs" => EligibleBranches.MaxBy(branch => branch.AvailableBytes ?? 0),
        "lfs" => EligibleBranches.MinBy(branch => branch.AvailableBytes ?? 0),
        _ => null,
    };
}
