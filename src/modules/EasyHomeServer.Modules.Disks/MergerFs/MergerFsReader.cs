using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.MergerFs;

/// <summary>
/// Finds the machine's mergerfs pools and reads what each one is actually doing.
/// </summary>
/// <remarks>
/// <para>
/// Two sources, because neither is sufficient alone. <c>mountinfo</c> knows a pool exists and
/// where; it does not know the branches, because <c>fsname=mergerfs</c> — which every sane setup
/// sets, to keep <c>df</c> readable — replaces the branch list in the source field with the
/// literal word "mergerfs". The branches come instead from the pool's own control file.
/// </para>
/// <para>
/// PID 1's mountinfo, not this process's, for the reason given in
/// <see cref="Disks.BlockDeviceReader"/>: the unit is sandboxed and its own view carries bind
/// mounts no one else has. The pool's *path*, though, is read directly. A systemd sandbox is an
/// <c>MS_SLAVE</c> namespace, so mounts the machine makes later still propagate into it — the
/// sandbox adds mounts, it does not hide them.
/// </para>
/// </remarks>
public sealed class MergerFsReader(ILogger<MergerFsReader> logger)
{
    /// <summary>The filesystem type mergerfs mounts appear as.</summary>
    private const string MergerFsType = "fuse.mergerfs";

    /// <summary>
    /// mergerfs answers queries about itself through xattrs on this file inside its own mount.
    /// </summary>
    private const string ControlFileName = ".mergerfs";

    private const string BranchesKey = "user.mergerfs.branches";
    private const string CreatePolicyKey = "user.mergerfs.category.create";
    private const string MinFreeSpaceKey = "user.mergerfs.minfreespace";
    private const string MoveOnEnoSpcKey = "user.mergerfs.moveonenospc";
    private const string VersionKey = "user.mergerfs.version";

    /// <summary>PID 1's mount table: the machine's, rather than this service's sandboxed one.</summary>
    private const string InitMountInfo = "/proc/1/mountinfo";

    /// <summary>Used only when PID 1's is unreadable — in a container, this process may be PID 1.</summary>
    private const string SelfMountInfo = "/proc/self/mountinfo";

    /// <summary>Reads every mergerfs pool on the machine.</summary>
    public ImmutableArray<MergerFsPool> Read()
    {
        try
        {
            var builder = ImmutableArray.CreateBuilder<MergerFsPool>();

            foreach (var mount in ReadMergerFsMounts())
            {
                builder.Add(ReadPool(mount.MountPoint, mount.Source));
            }

            return builder.ToImmutable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not read mergerfs pools.");

            return [];
        }
    }

    /// <summary>Reads one pool, asking the running mergerfs before trusting anything else.</summary>
    private MergerFsPool ReadPool(string mountPoint, string source)
    {
        var controlFile = Path.Combine(mountPoint, ControlFileName);

        var branchList = Xattr.TryGet(controlFile, BranchesKey);
        var fromRuntime = branchList is { Length: > 0 };

        if (!fromRuntime)
        {
            // An older mergerfs, or a pool that has gone away underneath us. The source field is
            // the only other place the branches could be, and only when fsname= did not mask it.
            branchList = source.Contains('/', StringComparison.Ordinal) ? source : null;

            logger.LogDebug(
                "mergerfs at {MountPoint} did not answer on its control file; falling back to the mount source.",
                mountPoint);
        }

        return new MergerFsPool
        {
            MountPoint = mountPoint,
            Branches = ParseBranches(branchList),
            CreatePolicy = Xattr.TryGet(controlFile, CreatePolicyKey).NullIfEmpty(),
            MinFreeSpaceBytes = ParseLong(Xattr.TryGet(controlFile, MinFreeSpaceKey)),
            MoveOnEnoSpc = ParseMoveOnEnoSpc(Xattr.TryGet(controlFile, MoveOnEnoSpcKey)),
            Version = ParseVersion(Xattr.TryGet(controlFile, VersionKey)),
            ConfigReadFromRuntime = fromRuntime,
        };
    }

    /// <summary>
    /// Parses a branch list, e.g. <c>/mnt/cache=RW:/mnt/data1=RW</c>.
    /// </summary>
    /// <remarks>
    /// Order is kept exactly as mergerfs gives it, because it is meaningful: <c>ff</c> means
    /// "first found", and reordering this list would misreport which disk new files land on.
    /// </remarks>
    private ImmutableArray<PoolBranch> ParseBranches(string? branchList)
    {
        if (string.IsNullOrWhiteSpace(branchList))
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<PoolBranch>();

        foreach (var entry in branchList.Split(':', StringSplitOptions.RemoveEmptyEntries
                                                    | StringSplitOptions.TrimEntries))
        {
            var separator = entry.LastIndexOf('=');

            var path = separator >= 0 ? entry[..separator] : entry;
            var mode = separator >= 0 ? entry[(separator + 1)..] : null;

            if (path.Length == 0)
            {
                continue;
            }

            var (total, available) = ReadUsage(path);

            builder.Add(new PoolBranch
            {
                Path = path,
                Mode = ParseMode(mode),
                TotalBytes = total,
                AvailableBytes = available,
            });
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Reads a branch's size and free space, or nulls when the branch is not there.
    /// </summary>
    /// <remarks>
    /// A branch mergerfs still lists but whose disk has gone is the case worth reporting, so a
    /// failure here is data rather than an error.
    /// </remarks>
    private (long? Total, long? Available) ReadUsage(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return (null, null);
            }

            var drive = new DriveInfo(path);

            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            logger.LogDebug(ex, "Could not read usage for branch {Branch}.", path);

            return (null, null);
        }
    }

    private static BranchMode ParseMode(string? mode) => mode?.ToUpperInvariant() switch
    {
        "RW" => BranchMode.ReadWrite,
        "RO" => BranchMode.ReadOnly,
        "NC" => BranchMode.NoCreate,
        null or "" => BranchMode.ReadWrite,
        _ => BranchMode.Unknown,
    };

    /// <summary>
    /// Normalises moveonenospc, whose "off" is spelled several ways.
    /// </summary>
    /// <remarks>
    /// The value is a policy name when on. A unit written as <c>moveonenospc=true</c> reads back
    /// as <c>mfs</c>, which is the point of asking the process rather than the unit.
    /// </remarks>
    private static string? ParseMoveOnEnoSpc(string? value) => value?.Trim() switch
    {
        null or "" or "false" or "off" => null,
        var policy => policy,
    };

    /// <summary>
    /// Returns the version only when it is one. Debian ships mergerfs built without its version
    /// stamped in, so both the real server and the test VM answer "unknown"; showing that to
    /// someone is worse than showing nothing.
    /// </summary>
    private static string? ParseVersion(string? value) =>
        value?.Trim() is { Length: > 0 } version
        && !version.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? version
            : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Finds the mergerfs mounts in the machine's mount table.</summary>
    private IEnumerable<(string MountPoint, string Source)> ReadMergerFsMounts()
    {
        var path = File.Exists(InitMountInfo) ? InitMountInfo : SelfMountInfo;

        foreach (var line in File.ReadLines(path))
        {
            if (TryParseMountInfo(line) is { } mount)
            {
                yield return mount;
            }
        }
    }

    /// <summary>
    /// Pulls the mount point, type and source out of a mountinfo line.
    /// </summary>
    /// <remarks>
    /// The format has a variable number of optional fields before a <c>-</c> separator, so the
    /// tail has to be found rather than indexed: mount point is field 4, and type and source are
    /// the first two fields after the separator.
    /// </remarks>
    private static (string MountPoint, string Source)? TryParseMountInfo(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var separator = Array.IndexOf(fields, "-");

        if (separator < 4 || separator + 2 >= fields.Length)
        {
            return null;
        }

        if (fields[separator + 1] != MergerFsType)
        {
            return null;
        }

        // mountinfo octal-escapes the characters that would otherwise break the field split.
        return (Unescape(fields[4]), Unescape(fields[separator + 2]));
    }

    /// <summary>Turns mountinfo's octal escapes (e.g. <c>\040</c> for a space) back into characters.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && TryParseOctal(value.AsSpan(i + 1, 3)) is { } code)
            {
                result.Append((char)code);
                i += 3;
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Parses three octal digits, as mountinfo writes them.
    /// </summary>
    /// <remarks>
    /// Octal, not decimal: a space is escaped <c>\040</c>, which read as decimal would be 40 —
    /// an open bracket. Every path with a space in it would come back subtly wrong.
    /// </remarks>
    private static int? TryParseOctal(ReadOnlySpan<char> digits)
    {
        var code = 0;

        foreach (var digit in digits)
        {
            if (digit is < '0' or > '7')
            {
                return null;
            }

            code = (code * 8) + (digit - '0');
        }

        return code > 0 ? code : null;
    }
}

internal static class StringExtensions
{
    /// <summary>Collapses a whitespace-only value to null, so the UI has one "absent" to check.</summary>
    public static string? NullIfEmpty(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
