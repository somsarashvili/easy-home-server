using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Disks.SnapRaid;

/// <summary>One row of SnapRAID's per-disk status table.</summary>
public sealed record SnapRaidDiskStatus
{
    /// <summary>SnapRAID's name for the disk, e.g. <c>d1</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Files it protects.</summary>
    public required long Files { get; init; }

    /// <summary>Files stored in more than one piece.</summary>
    public required long FragmentedFiles { get; init; }

    /// <summary>Fragments beyond one per file. Large numbers mean slow reads on a rotating disk.</summary>
    public required long ExcessFragments { get; init; }

    /// <summary>Space SnapRAID considers wasted, in GB. Can be negative; SnapRAID prints it that way.</summary>
    public required double WastedGb { get; init; }

    /// <summary>Space in use, in GB.</summary>
    public required long UsedGb { get; init; }

    /// <summary>Space free, in GB.</summary>
    public required long FreeGb { get; init; }

    /// <summary>Percentage of the disk in use.</summary>
    public required int UsePercent { get; init; }
}

/// <summary>
/// What <c>snapraid status</c> reports about the array.
/// </summary>
/// <remarks>
/// Parsed from a report meant for a person, because SnapRAID has no machine-readable output. Every
/// field is therefore optional in principle: a version that words a line differently should leave
/// that one field unknown rather than take the whole page down.
/// </remarks>
public sealed record SnapRaidStatus
{
    /// <summary>When this was read.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The per-disk table.</summary>
    public ImmutableArray<SnapRaidDiskStatus> Disks { get; init; } = [];

    /// <summary>
    /// Whether every change is protected by parity.
    /// </summary>
    /// <remarks>
    /// The single most important fact here. False means files exist that a disk failure would
    /// lose, which is the normal state between syncs and an alarming one if it persists.
    /// </remarks>
    public bool IsFullySynced { get; init; } = true;

    /// <summary>How far an interrupted or running sync got, when SnapRAID says.</summary>
    public int? SyncProgressPercent { get; init; }

    /// <summary>Percentage of the array never scrubbed, or verified too long ago to count.</summary>
    public int? NotScrubbedPercent { get; init; }

    /// <summary>Age in days of the least recently scrubbed block.</summary>
    public int? OldestScrubDays { get; init; }

    /// <summary>Age in days of the median block.</summary>
    public int? MedianScrubDays { get; init; }

    /// <summary>
    /// Blocks SnapRAID knows are damaged, found by a previous scrub.
    /// </summary>
    /// <remarks>
    /// These are silent: the array serves reads and syncs normally, and nothing surfaces them
    /// except running status. That is exactly why this belongs on a page someone looks at.
    /// </remarks>
    public int ErrorCount { get; init; }

    /// <summary>Whether SnapRAID wants a rehash, after a hash algorithm change.</summary>
    public bool RehashNeeded { get; init; }

    /// <summary>The report as printed, for when the parse missed something.</summary>
    public string RawOutput { get; init; } = string.Empty;

    /// <summary>Files across every disk.</summary>
    public long TotalFiles => Disks.Sum(disk => disk.Files);

    /// <summary>Whether anything here warrants attention.</summary>
    public bool NeedsAttention => !IsFullySynced || ErrorCount > 0 || RehashNeeded;
}
