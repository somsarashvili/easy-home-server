using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.SystemInfo.Metrics;

/// <summary>
/// Reads metrics straight from <c>/proc</c> and <c>/sys</c>. No shelling out: parsing these
/// files costs microseconds, while spawning <c>top</c> or <c>df</c> every two seconds would
/// cost a process launch each time and give less.
/// </summary>
/// <remarks>
/// <para>
/// Every read is individually fault-tolerant. Files under /proc are synthesised on demand and
/// can vanish, truncate or change shape between kernels; a container may not expose them at
/// all. Any single reader failing degrades that one card to "unavailable" rather than losing
/// the whole sample.
/// </para>
/// <para>
/// Counter-based metrics (CPU, network) are meaningless as absolute values — the kernel
/// reports monotonic totals since boot. This class holds the previous reading and returns
/// rates computed from the delta, which is why it is stateful and registered as a singleton.
/// </para>
/// </remarks>
public sealed class ProcReader(ILogger<ProcReader> logger)
{
    private const string ProcStat = "/proc/stat";
    private const string ProcMemInfo = "/proc/meminfo";
    private const string ProcLoadAvg = "/proc/loadavg";
    private const string ProcUptime = "/proc/uptime";
    private const string ProcNetDev = "/proc/net/dev";
    private const string ProcCpuInfo = "/proc/cpuinfo";

    /// <summary>
    /// PID 1's mount table — the machine's own view, rather than this service's.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>/proc/self/mountinfo</c>. The systemd unit uses
    /// <c>StateDirectory=</c> and <c>PrivateTmp=</c>, which put this process in its own mount
    /// namespace with extra bind mounts for <c>/var/lib/easyhomeserver</c> and <c>/var/tmp</c>.
    /// Those are artefacts of this service's sandbox — they do not exist for anyone else on the
    /// machine, and listing them shows three rows all reporting the same device and size as
    /// <c>/</c>.
    /// <para>
    /// <c>mountinfo</c>, not <c>mounts</c>: only mountinfo carries the root field that
    /// identifies a bind mount. Reading PID 1 alone is not enough — a bind mount in the init
    /// namespace (a Docker volume, an admin's own <c>mount --bind</c>) would still show up as a
    /// phantom duplicate.
    /// </para>
    /// </remarks>
    private const string InitMountInfo = "/proc/1/mountinfo";

    /// <summary>Fallback when PID 1's table cannot be read (unprivileged, or PID 1 is us in a container).</summary>
    private const string SelfMountInfo = "/proc/self/mountinfo";

    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private CpuTimes[]? _previousCpuTimes;
    private Dictionary<string, (long Received, long Transmitted)>? _previousNetBytes;
    private DateTimeOffset _previousNetAt;

    /// <summary>True when this platform exposes procfs. False on a developer's macOS box.</summary>
    public static bool IsSupported => IsLinux && Directory.Exists("/proc");

    /// <summary>
    /// Takes a full sample. The first call establishes counter baselines, so its CPU and
    /// network rates are null/zero and the second sample is the first meaningful one.
    /// </summary>
    public SystemSnapshot Read()
    {
        var (total, perCore) = ReadCpuUsage();

        return new SystemSnapshot
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            CpuTotalPercent = total,
            CpuPerCorePercent = perCore,
            Memory = ReadMemory(),
            Load = ReadLoadAverage(),
            Uptime = ReadUptime(),
            Mounts = ReadMounts(),
            Interfaces = ReadInterfaces(),
        };
    }

    /// <summary>Reads the machine's static identity. Cheap enough to call once at startup.</summary>
    public SystemIdentity ReadIdentity()
    {
        return new SystemIdentity
        {
            HostName = ReadTrimmed("/proc/sys/kernel/hostname") ?? Environment.MachineName,
            KernelVersion = ReadTrimmed("/proc/sys/kernel/osrelease") ?? RuntimeInformation.OSDescription,
            OperatingSystem = ReadOsPrettyName() ?? RuntimeInformation.OSDescription,
            CpuModel = ReadCpuModel(),
            CoreCount = Environment.ProcessorCount,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    /// <summary>
    /// Reads a file that may legitimately not exist. Returns null on any failure and logs at
    /// debug: a missing /proc entry is a normal platform difference, not an error worth waking
    /// anyone for.
    /// </summary>
    private string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogDebug(ex, "Could not read {Path}.", path);

            return null;
        }
    }

    private string? ReadTrimmed(string path) => TryReadAllText(path)?.Trim() is { Length: > 0 } value ? value : null;

    private (double? Total, ImmutableArray<double> PerCore) ReadCpuUsage()
    {
        var content = TryReadAllText(ProcStat);

        if (content is null)
        {
            return (null, []);
        }

        var current = ParseCpuTimes(content);

        if (current.Length == 0)
        {
            return (null, []);
        }

        var previous = _previousCpuTimes;
        _previousCpuTimes = current;

        // First sample after start: there is no delta to compute a rate from yet.
        if (previous is null || previous.Length != current.Length)
        {
            return (null, []);
        }

        var usages = new double[current.Length];

        for (var i = 0; i < current.Length; i++)
        {
            usages[i] = ComputeUsage(previous[i], current[i]);
        }

        // Index 0 is the aggregate "cpu" line; the rest are individual cores.
        return (usages[0], [.. usages.Skip(1)]);
    }

    private static CpuTimes[] ParseCpuTimes(string content)
    {
        var results = new List<CpuTimes>();

        foreach (var line in content.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("cpu"))
            {
                // The cpu lines are contiguous and first; everything after is other counters.
                break;
            }

            var fields = line.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // name + at least user/nice/system/idle
            if (fields.Length < 5)
            {
                continue;
            }

            long idle = 0;
            long total = 0;

            for (var i = 1; i < fields.Length; i++)
            {
                if (!long.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                total += value;

                // Fields 4 and 5 are idle and iowait; both are time the CPU was not working.
                if (i is 4 or 5)
                {
                    idle += value;
                }
            }

            results.Add(new CpuTimes(idle, total));
        }

        return [.. results];
    }

    private static double ComputeUsage(CpuTimes previous, CpuTimes current)
    {
        var totalDelta = current.Total - previous.Total;
        var idleDelta = current.Idle - previous.Idle;

        // Counters reset (or the clock did not advance): report idle rather than a wild number.
        if (totalDelta <= 0)
        {
            return 0;
        }

        var usage = (1.0 - (double)idleDelta / totalDelta) * 100.0;

        return Math.Clamp(usage, 0, 100);
    }

    private MemoryUsage? ReadMemory()
    {
        var content = TryReadAllText(ProcMemInfo);

        if (content is null)
        {
            return null;
        }

        var values = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var line in content.AsSpan().EnumerateLines())
        {
            var text = line.ToString();
            var colon = text.IndexOf(':');

            if (colon <= 0)
            {
                continue;
            }

            var key = text[..colon];
            var rest = text[(colon + 1)..].Trim();
            var space = rest.IndexOf(' ');
            var number = space > 0 ? rest[..space] : rest;

            // Values are in kB except a couple of entries that carry no unit; both parse the same.
            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                values[key] = parsed * 1024;
            }
        }

        if (!values.TryGetValue("MemTotal", out var total) || total <= 0)
        {
            return null;
        }

        // MemAvailable has been present since Linux 3.14 and is a far better estimate than
        // MemFree + Cached; fall back only for ancient kernels.
        if (!values.TryGetValue("MemAvailable", out var available))
        {
            values.TryGetValue("MemFree", out var free);
            values.TryGetValue("Cached", out var fallbackCached);
            available = free + fallbackCached;
        }

        values.TryGetValue("Cached", out var cached);
        values.TryGetValue("Buffers", out var buffers);
        values.TryGetValue("SwapTotal", out var swapTotal);
        values.TryGetValue("SwapFree", out var swapFree);

        return new MemoryUsage
        {
            TotalBytes = total,
            AvailableBytes = Math.Min(available, total),
            CachedBytes = cached + buffers,
            SwapTotalBytes = swapTotal,
            SwapUsedBytes = Math.Max(0, swapTotal - swapFree),
        };
    }

    private LoadAverage? ReadLoadAverage()
    {
        var content = ReadTrimmed(ProcLoadAvg);

        if (content is null)
        {
            return null;
        }

        var fields = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 3)
        {
            return null;
        }

        if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one)
            || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var five)
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fifteen))
        {
            return null;
        }

        return new LoadAverage { OneMinute = one, FiveMinutes = five, FifteenMinutes = fifteen };
    }

    private TimeSpan? ReadUptime()
    {
        var content = ReadTrimmed(ProcUptime);

        if (content is null)
        {
            return null;
        }

        var first = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (first is null
            || !double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private ImmutableArray<MountUsage> ReadMounts()
    {
        // See InitMountInfo: our own mount table is polluted by this service's sandbox.
        var content = TryReadAllText(InitMountInfo) ?? TryReadAllText(SelfMountInfo);

        if (content is null)
        {
            return [];
        }

        var results = new List<MountUsage>();

        foreach (var line in content.AsSpan().EnumerateLines())
        {
            if (!TryParseMountInfo(line.ToString(), out var device, out var mountPoint, out var fileSystem))
            {
                continue;
            }

            if (!IsInterestingFileSystem(fileSystem, device))
            {
                continue;
            }

            try
            {
                var info = new DriveInfo(mountPoint);

                // Pseudo-filesystems that slipped the filter report zero size.
                if (!info.IsReady || info.TotalSize <= 0)
                {
                    continue;
                }

                results.Add(new MountUsage
                {
                    MountPoint = mountPoint,
                    Device = device,
                    FileSystem = fileSystem,
                    TotalBytes = info.TotalSize,
                    FreeBytes = info.TotalFreeSpace,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A mount can disappear between reading /proc/mounts and stat'ing it, and an
                // unreachable network mount throws here rather than blocking forever.
                logger.LogDebug(ex, "Could not stat mount {MountPoint}.", mountPoint);
            }
        }

        return [.. results.DistinctBy(m => m.MountPoint).OrderBy(m => m.MountPoint, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Parses one <c>/proc/*/mountinfo</c> line, keeping only whole-filesystem mounts.
    /// </summary>
    /// <remarks>
    /// Format, per Documentation/filesystems/proc.rst:
    /// <code>
    /// 36 25 8:2 /      /boot ext4 rw,relatime - ext4  /dev/sda2 rw    ← real mount
    /// 42 25 8:3 /state /srv  ext4 rw,relatime - ext4  /dev/sda3 rw    ← bind mount
    ///  0  1  2  3      4     5    6           7 8     9
    /// </code>
    /// Field 3 is the <em>root</em>: which subtree of the filesystem is mounted. It is
    /// <c>/</c> for a real mount and a subdirectory for a bind mount, which is the only
    /// reliable way to tell the two apart — <c>/proc/mounts</c> omits this, which is why a
    /// bind mount there is indistinguishable from the filesystem it shadows.
    /// <para>
    /// The optional fields between 6 and the <c>-</c> separator are variable in number, so the
    /// separator must be located rather than assumed at a fixed index.
    /// </para>
    /// </remarks>
    private static bool TryParseMountInfo(string line, out string device, out string mountPoint, out string fileSystem)
    {
        device = string.Empty;
        mountPoint = string.Empty;
        fileSystem = string.Empty;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 10)
        {
            return false;
        }

        var separator = Array.IndexOf(fields, "-");

        // The separator must exist and leave room for fstype and source after it.
        if (separator < 6 || separator + 2 >= fields.Length)
        {
            return false;
        }

        // Anything but "/" is a bind mount: a view of a subtree that is already reported by the
        // filesystem's real mount, with identical size and usage. Listing them means the same
        // disk appears several times over.
        if (fields[3] != "/")
        {
            return false;
        }

        mountPoint = UnescapeMountPath(fields[4]);
        fileSystem = fields[separator + 1];
        device = UnescapeMountPath(fields[separator + 2]);

        return true;
    }

    /// <summary>
    /// Keeps real, user-meaningful filesystems and drops the dozens of kernel bookkeeping
    /// mounts (cgroup, sysfs, tracefs) that would otherwise bury them.
    /// </summary>
    private static bool IsInterestingFileSystem(string fileSystem, string device)
    {
        string[] realFileSystems =
        [
            "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "jfs", "reiserfs",
            "vfat", "exfat", "ntfs", "ntfs3", "hfsplus", "apfs", "iso9660", "udf",
            "nfs", "nfs4", "cifs", "smb3", "fuseblk",
            // A container's root really is an overlay; without this the dev container shows
            // no filesystems at all.
            "overlay",
        ];

        if (!realFileSystems.Contains(fileSystem, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // Bind mounts and loop-mounted snap images duplicate storage that is already listed.
        return !device.StartsWith("/dev/loop", StringComparison.Ordinal);
    }

    /// <summary>
    /// Decodes the octal escapes the kernel writes for spaces, tabs, newlines and backslashes
    /// in mount paths — a mount point like <c>/mnt/my drive</c> appears as <c>/mnt/my\040drive</c>.
    /// </summary>
    private static string UnescapeMountPath(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && int.TryParse(value.AsSpan(i + 1, 3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                try
                {
                    result.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                    i += 3;

                    continue;
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    // Not a valid octal escape after all; fall through and treat as a literal.
                }
            }

            result.Append(value[i]);
        }

        return result.ToString();
    }

    private ImmutableArray<InterfaceThroughput> ReadInterfaces()
    {
        var content = TryReadAllText(ProcNetDev);

        if (content is null)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, (long Received, long Transmitted)>(StringComparer.Ordinal);

        foreach (var line in content.AsSpan().EnumerateLines())
        {
            var text = line.ToString();
            var colon = text.IndexOf(':');

            // The two header lines have no colon in the name position.
            if (colon <= 0)
            {
                continue;
            }

            var name = text[..colon].Trim();

            if (name.Length == 0 || name == "lo" || !IsInterfaceUp(name))
            {
                continue;
            }

            var fields = text[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // receive: bytes packets errs drop fifo frame compressed multicast (8)
            // transmit: bytes ... — transmit bytes is field 8.
            if (fields.Length < 9
                || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var received)
                || !long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var transmitted))
            {
                continue;
            }

            current[name] = (received, transmitted);
        }

        var previous = _previousNetBytes;
        var previousAt = _previousNetAt;
        _previousNetBytes = current;
        _previousNetAt = now;

        var elapsed = (now - previousAt).TotalSeconds;
        var results = new List<InterfaceThroughput>();

        foreach (var (name, bytes) in current)
        {
            double receiveRate = 0;
            double transmitRate = 0;

            if (previous is not null && previous.TryGetValue(name, out var before) && elapsed > 0)
            {
                // Counters are 64-bit and monotonic, but wrap on 32-bit kernels and reset when
                // an interface is recreated. A negative delta means "unknown", not "negative".
                receiveRate = Math.Max(0, (bytes.Received - before.Received) / elapsed);
                transmitRate = Math.Max(0, (bytes.Transmitted - before.Transmitted) / elapsed);
            }

            results.Add(new InterfaceThroughput
            {
                Name = name,
                ReceiveBytesPerSecond = receiveRate,
                TransmitBytesPerSecond = transmitRate,
                TotalReceivedBytes = bytes.Received,
                TotalTransmittedBytes = bytes.Transmitted,
            });
        }

        return [.. results.OrderBy(i => i.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether an interface is worth showing, from <c>/sys/class/net/{name}/operstate</c>.
    /// </summary>
    /// <remarks>
    /// A stock kernel registers a pile of tunnel devices — gre0, erspan0, sit0, ip_vti0 — that
    /// are always present and always "down". Listing them buries the one or two interfaces that
    /// carry traffic. Anything not explicitly "down" is kept: some interfaces legitimately
    /// report "unknown", and this filter should never hide a working NIC.
    /// </remarks>
    private bool IsInterfaceUp(string name)
    {
        // The name comes from /proc/net/dev, but it lands in a file path, so refuse anything
        // that could climb out of /sys/class/net.
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var state = ReadTrimmed($"/sys/class/net/{name}/operstate");

        // No operstate at all (older kernels, odd virtual devices): show it rather than guess.
        return !string.Equals(state, "down", StringComparison.OrdinalIgnoreCase);
    }

    private string? ReadOsPrettyName()
    {
        var content = TryReadAllText("/etc/os-release");

        if (content is null)
        {
            return null;
        }

        foreach (var line in content.AsSpan().EnumerateLines())
        {
            var text = line.ToString();

            if (!text.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                continue;
            }

            return text["PRETTY_NAME=".Length..].Trim().Trim('"');
        }

        return null;
    }

    private string? ReadCpuModel()
    {
        var content = TryReadAllText(ProcCpuInfo);

        if (content is null)
        {
            return null;
        }

        // x86 reports "model name". arm64 often has none of these — it reports only
        // "CPU implementer: 0x61" and friends, which are raw hex IDs and worse than useless to
        // show an operator, so they are deliberately not consulted; the caller falls back.
        string[] keys = ["model name", "Model", "Hardware", "cpu model"];

        foreach (var key in keys)
        {
            foreach (var line in content.AsSpan().EnumerateLines())
            {
                var text = line.ToString();
                var colon = text.IndexOf(':');

                if (colon <= 0)
                {
                    continue;
                }

                if (!string.Equals(text[..colon].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = text[(colon + 1)..].Trim();

                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private readonly record struct CpuTimes(long Idle, long Total);
}
