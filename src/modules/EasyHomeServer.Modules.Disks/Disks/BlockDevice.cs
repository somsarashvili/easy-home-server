using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>
/// A block device: a whole disk, or a partition of one.
/// </summary>
/// <remarks>
/// Disks and partitions are one type rather than two because the kernel treats them alike and
/// lsblk reports them alike — a partition is simply a device with a parent. Splitting them would
/// mean duplicating every field for the sake of a distinction that only matters in a handful of
/// places, each of which asks <see cref="Kind"/>.
/// </remarks>
public sealed record BlockDevice
{
    /// <summary>Kernel name, for example <c>sda1</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Full device path, for example <c>/dev/sda1</c>.</summary>
    public required string Path { get; init; }

    /// <summary>What sort of device this is.</summary>
    public required DeviceKind Kind { get; init; }

    /// <summary>Size in bytes. Read with <c>lsblk -b</c> rather than its pre-formatted "64G".</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Hardware model, when the device reports one. Virtual disks usually do not.</summary>
    public string? Model { get; init; }

    /// <summary>Serial number, for telling identical disks apart.</summary>
    public string? Serial { get; init; }

    /// <summary>Vendor string.</summary>
    public string? Vendor { get; init; }

    /// <summary>How it is attached: sata, nvme, usb, virtio.</summary>
    public string? Transport { get; init; }

    /// <summary>Partition table type on a whole disk: gpt, dos, or null for an unpartitioned one.</summary>
    public string? PartitionTable { get; init; }

    /// <summary>
    /// Whether the kernel calls it rotational.
    /// </summary>
    /// <remarks>
    /// Believe it only for real hardware. A virtio disk reports rotational even though it is a
    /// file on an SSD, so this says what the kernel thinks rather than what is true, and the UI
    /// should not turn it into a confident "HDD".
    /// </remarks>
    public bool IsRotational { get; init; }

    /// <summary>Whether the device is removable, such as a USB stick.</summary>
    public bool IsRemovable { get; init; }

    /// <summary>Whether the kernel has it read-only.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>Filesystem on it, or null if unformatted.</summary>
    public string? FileSystem { get; init; }

    /// <summary>Filesystem label.</summary>
    public string? Label { get; init; }

    /// <summary>Filesystem UUID — what fstab should reference, since kernel names can move between boots.</summary>
    public string? Uuid { get; init; }

    /// <summary>Where it is mounted. More than one for a bind-mounted filesystem.</summary>
    public ImmutableArray<string> MountPoints { get; init; } = [];

    /// <summary>Total size of the filesystem, which is slightly less than the partition.</summary>
    public long? FsSizeBytes { get; init; }

    /// <summary>Free space, when mounted. Null when not, since it cannot be known without mounting.</summary>
    public long? FsAvailableBytes { get; init; }

    /// <summary>Used space, when mounted.</summary>
    public long? FsUsedBytes { get; init; }

    /// <summary>Partitions of a disk, or empty.</summary>
    public ImmutableArray<BlockDevice> Children { get; init; } = [];

    /// <summary>True when it is mounted somewhere.</summary>
    public bool IsMounted => MountPoints.Length > 0;

    /// <summary>First mount point, or null.</summary>
    public string? MountPoint => MountPoints.Length > 0 ? MountPoints[0] : null;

    /// <summary>Used space as a percentage, when mounted.</summary>
    public double? UsedPercent => FsSizeBytes is > 0 && FsUsedBytes is not null
        ? FsUsedBytes.Value * 100.0 / FsSizeBytes.Value
        : null;

    /// <summary>True when it holds no filesystem and no partition table — a blank disk.</summary>
    public bool IsBlank => FileSystem is null && PartitionTable is null && Children.Length == 0;

    /// <summary>
    /// True when this device, or anything on it, carries a mounted filesystem.
    /// </summary>
    /// <remarks>
    /// The question anything destructive must ask. A disk is not safe to touch just because the
    /// disk itself is unmounted — its partitions are what get mounted.
    /// </remarks>
    public bool IsInUse => IsMounted || Children.Any(c => c.IsInUse);

    /// <summary>
    /// True when this device holds the running system: <c>/</c>, <c>/boot</c> or swap.
    /// </summary>
    /// <remarks>
    /// Everything destructive refuses on this. Swap counts: reformatting the swap partition
    /// underneath a running kernel is its own kind of bad day.
    /// </remarks>
    public bool IsSystemDisk =>
        MountPoints.Any(m => m is "/" or "/boot" or "/boot/efi" || m.StartsWith("[SWAP]", StringComparison.Ordinal))
        || Children.Any(c => c.IsSystemDisk);
}

/// <summary>What sort of block device something is.</summary>
public enum DeviceKind
{
    /// <summary>A whole disk.</summary>
    Disk,

    /// <summary>A partition of a disk.</summary>
    Partition,

    /// <summary>Optical drive.</summary>
    Rom,

    /// <summary>Loop device, backing a file.</summary>
    Loop,

    /// <summary>LVM logical volume.</summary>
    Lvm,

    /// <summary>Encrypted container, such as LUKS.</summary>
    Crypt,

    /// <summary>Software RAID array.</summary>
    Raid,

    /// <summary>Something lsblk reported that this does not model.</summary>
    Other,
}
