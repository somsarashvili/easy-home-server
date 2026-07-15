using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EasyHomeServer.Modules.Disks.SnapRaid;

/// <summary>
/// Reads <c>snapraid status</c>'s report.
/// </summary>
/// <remarks>
/// <para>
/// SnapRAID emits no JSON, so this parses prose. It is written to fail one field at a time: an
/// unrecognised line leaves its field null and everything else stands. The raw report is kept on
/// the result so a person can read what the parser could not.
/// </para>
/// <para>
/// The wording is matched loosely for the same reason. "No error detected." and "DANGER! In the
/// array there are 2 errors!" are the two known spellings, and the count is what matters.
/// </para>
/// </remarks>
public static partial class SnapRaidStatusParser
{
    /// <summary>
    /// A per-disk row: counts, then a percentage, then the disk's name.
    /// </summary>
    /// <remarks>
    /// Anchored on the trailing "NN% name" rather than on column positions, which shift with the
    /// width of the numbers. The totals row has no name and so does not match, which is what we
    /// want — its numbers are sums this can compute itself.
    /// </remarks>
    [GeneratedRegex(@"^\s*(?<files>\d+)\s+(?<fragmented>\d+)\s+(?<excess>\d+)\s+"
                    + @"(?<wasted>-?[\d.]+)\s+(?<used>\d+)\s+(?<free>\d+)\s+(?<use>\d+)%\s+(?<name>\S+)\s*$")]
    private static partial Regex DiskRow { get; }

    [GeneratedRegex(@"sync in progress at (?<percent>\d+)%", RegexOptions.IgnoreCase)]
    private static partial Regex SyncProgress { get; }

    [GeneratedRegex(@"(?<percent>\d+)%\s+of the array is not scrubbed", RegexOptions.IgnoreCase)]
    private static partial Regex NotScrubbed { get; }

    [GeneratedRegex(@"there are (?<count>\d+) errors", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorCount { get; }

    [GeneratedRegex(@"oldest block was scrubbed (?<oldest>\d+) days? ago, the median (?<median>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScrubAge { get; }

    /// <summary>Turns a report into a <see cref="SnapRaidStatus"/>.</summary>
    public static SnapRaidStatus Parse(string output, DateTimeOffset timestampUtc)
    {
        var disks = ImmutableArray.CreateBuilder<SnapRaidDiskStatus>();

        var fullySynced = true;
        int? syncProgress = null;
        int? notScrubbed = null;
        int? oldestScrub = null;
        int? medianScrub = null;
        var errors = 0;
        var rehash = false;

        foreach (var line in output.Split('\n'))
        {
            if (DiskRow.Match(line) is { Success: true } row)
            {
                disks.Add(ParseDiskRow(row));

                continue;
            }

            // "WARNING! The array is NOT fully synced." — the headline fact.
            if (line.Contains("NOT fully synced", StringComparison.OrdinalIgnoreCase))
            {
                fullySynced = false;
            }

            if (SyncProgress.Match(line) is { Success: true } sync)
            {
                syncProgress = ParseInt(sync.Groups["percent"].Value);

                // A sync in progress means the parity does not yet cover everything.
                fullySynced = false;
            }

            if (NotScrubbed.Match(line) is { Success: true } scrub)
            {
                notScrubbed = ParseInt(scrub.Groups["percent"].Value);
            }

            if (ScrubAge.Match(line) is { Success: true } age)
            {
                oldestScrub = ParseInt(age.Groups["oldest"].Value);
                medianScrub = ParseInt(age.Groups["median"].Value);
            }

            if (ErrorCount.Match(line) is { Success: true } error)
            {
                errors = ParseInt(error.Groups["count"].Value) ?? 0;
            }

            // "No rehash is in progress or needed." is the healthy case; anything else about a
            // rehash means there is one.
            if (line.Contains("rehash", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("No rehash", StringComparison.OrdinalIgnoreCase))
            {
                rehash = true;
            }
        }

        return new SnapRaidStatus
        {
            TimestampUtc = timestampUtc,
            Disks = disks.ToImmutable(),
            IsFullySynced = fullySynced,
            SyncProgressPercent = syncProgress,
            NotScrubbedPercent = notScrubbed,
            OldestScrubDays = oldestScrub,
            MedianScrubDays = medianScrub,
            ErrorCount = errors,
            RehashNeeded = rehash,
            RawOutput = output,
        };
    }

    private static SnapRaidDiskStatus ParseDiskRow(Match row) => new()
    {
        Name = row.Groups["name"].Value,
        Files = ParseLong(row.Groups["files"].Value) ?? 0,
        FragmentedFiles = ParseLong(row.Groups["fragmented"].Value) ?? 0,
        ExcessFragments = ParseLong(row.Groups["excess"].Value) ?? 0,
        WastedGb = ParseDouble(row.Groups["wasted"].Value) ?? 0,
        UsedGb = ParseLong(row.Groups["used"].Value) ?? 0,
        FreeGb = ParseLong(row.Groups["free"].Value) ?? 0,
        UsePercent = ParseInt(row.Groups["use"].Value) ?? 0,
    };

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
