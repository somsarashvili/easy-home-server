using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>The machine's storage as of one reading.</summary>
public sealed record DiskSnapshot
{
    /// <summary>When it was read.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Every block device, as a tree of disks and their partitions.</summary>
    public ImmutableArray<BlockDevice> Devices { get; init; } = [];

    /// <summary>
    /// Whole disks worth showing.
    /// </summary>
    /// <remarks>
    /// Loop devices are excluded: a machine running snap packages has dozens, each a squashfs
    /// image of an application, and none of them is a disk anyone manages. Optical drives are
    /// excluded for the same reason — an empty sr0 is on every VM and is only noise.
    /// </remarks>
    public IEnumerable<BlockDevice> Disks =>
        Devices.Where(d => d.Kind == DeviceKind.Disk);

    /// <summary>Total capacity of the disks shown.</summary>
    public long TotalBytes => Disks.Sum(d => d.SizeBytes);

    /// <summary>Disks with no partition table and no filesystem — ready to be prepared.</summary>
    public IEnumerable<BlockDevice> BlankDisks => Disks.Where(d => d.IsBlank);
}
