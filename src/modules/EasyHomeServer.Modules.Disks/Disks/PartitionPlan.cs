using System.Collections.Immutable;
using System.Globalization;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>One partition to create.</summary>
public sealed record PartitionSpec
{
    /// <summary>
    /// How big to make it, or null to use whatever is left.
    /// </summary>
    /// <remarks>
    /// Only the last partition may take the remainder: sgdisk fills from the first free sector, so
    /// a greedy partition in the middle leaves nothing for the ones after it.
    /// </remarks>
    public long? SizeBytes { get; init; }

    /// <summary>The filesystem to make on it.</summary>
    public required string FileSystemId { get; init; }

    /// <summary>Its label, or null for none.</summary>
    public string? Label { get; init; }

    /// <summary>Whether this one takes whatever space is left.</summary>
    public bool TakesRemainder => SizeBytes is null;
}

/// <summary>
/// A disk's whole partition layout, as asked for.
/// </summary>
/// <remarks>
/// Applied by rewriting the partition table from empty rather than by editing it. Editing in place
/// is what a partition editor does, and it is a much larger problem: moving data, shrinking
/// filesystems, and being interrupted halfway. Everything here destroys the disk and says so.
/// </remarks>
public sealed record PartitionPlan
{
    /// <summary>
    /// GPT's own overhead: a primary header and table at the front, a backup set at the very end.
    /// </summary>
    /// <remarks>
    /// 1 MiB each end is more than the 33 sectors GPT needs, and is what sgdisk's default alignment
    /// consumes anyway. Reserving it here keeps "the sizes add up" honest at the point someone types
    /// them, rather than sgdisk refusing the last partition after the disk has been wiped.
    /// </remarks>
    public const long GptOverheadBytes = 2 * 1024 * 1024;

    /// <summary>The partitions, in order, first at the start of the disk.</summary>
    public ImmutableArray<PartitionSpec> Partitions { get; init; } = [];

    /// <summary>Space the sized partitions ask for.</summary>
    public long RequestedBytes => Partitions.Sum(p => p.SizeBytes ?? 0);

    /// <summary>
    /// Everything wrong with the plan for a given disk, as sentences.
    /// </summary>
    /// <remarks>
    /// All at once rather than the first: these are answers to a form.
    /// </remarks>
    public ImmutableArray<string> Validate(long diskSizeBytes)
    {
        var problems = ImmutableArray.CreateBuilder<string>();

        if (Partitions.Length == 0)
        {
            problems.Add("Add at least one partition.");

            return problems.ToImmutable();
        }

        var usable = diskSizeBytes - GptOverheadBytes;

        foreach (var (partition, index) in Partitions.Select((p, i) => (p, i)))
        {
            var number = index + 1;

            if (partition.SizeBytes is { } size && size <= 0)
            {
                problems.Add($"Partition {number} has no size.");
            }

            // sgdisk allocates from the first free sector, so only the last can be open-ended.
            if (partition.TakesRemainder && index != Partitions.Length - 1)
            {
                problems.Add($"Only the last partition can take the remaining space; partition {number} cannot.");
            }

            if (partition.Label is { Length: > 0 } label && label.Length > 16)
            {
                // ext4 caps labels at 16 bytes and silently truncates; better to say so.
                problems.Add($"Partition {number}'s label is longer than 16 characters, which ext4 would cut short.");
            }

            if (FileSystemId(partition) is null)
            {
                problems.Add($"Partition {number} has no filesystem chosen.");
            }
        }

        if (RequestedBytes > usable)
        {
            problems.Add(
                $"The partitions ask for {Format.Bytes(RequestedBytes)}, and only "
                + $"{Format.Bytes(usable)} is usable — the partition table itself takes the rest.");
        }

        // A trailing "rest" partition with nothing left for it would be created 0 bytes long, or
        // refused, after the disk has already been wiped.
        if (Partitions[^1].TakesRemainder && RequestedBytes >= usable)
        {
            problems.Add("The last partition takes the remaining space, but the others have used all of it.");
        }

        return problems.ToImmutable();
    }

    /// <summary>Space left for a partition that takes the remainder.</summary>
    public long RemainderBytes(long diskSizeBytes) =>
        Math.Max(0, diskSizeBytes - GptOverheadBytes - RequestedBytes);

    private static string? FileSystemId(PartitionSpec partition) =>
        string.IsNullOrWhiteSpace(partition.FileSystemId) ? null : partition.FileSystemId;

    /// <summary>
    /// The sgdisk size argument for a partition: <c>+512M</c>, or empty for the remainder.
    /// </summary>
    /// <remarks>
    /// Given in MiB rather than sectors so the command shown to a person is the one they asked for.
    /// Rounded up, so asking for a size never quietly delivers less.
    /// </remarks>
    public static string SizeArgument(PartitionSpec partition)
    {
        if (partition.SizeBytes is not { } size)
        {
            return "0";
        }

        const long Mebibyte = 1024 * 1024;

        var mebibytes = (size + Mebibyte - 1) / Mebibyte;

        return "+" + mebibytes.ToString(CultureInfo.InvariantCulture) + "M";
    }
}
