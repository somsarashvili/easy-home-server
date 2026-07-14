using System.Collections.Immutable;

namespace EasyHomeServer.Modules.SystemInfo.Metrics;

/// <summary>
/// One sample of the machine's state, published on the event bus by
/// <see cref="SystemSampler"/> and rendered by the module's page.
/// </summary>
/// <remarks>
/// This type is the module's public event contract. It lives in the module assembly because
/// only this module publishes and consumes it; an event that another module needs to subscribe
/// to would move to a small shared contracts assembly both can reference.
/// </remarks>
public sealed record SystemSnapshot
{
    /// <summary>When the sample was taken.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Aggregate CPU utilisation across all cores, 0-100, or null if unavailable.</summary>
    public double? CpuTotalPercent { get; init; }

    /// <summary>Per-core utilisation, 0-100, in core order.</summary>
    public ImmutableArray<double> CpuPerCorePercent { get; init; } = [];

    /// <summary>Memory usage, or null if <c>/proc/meminfo</c> could not be read.</summary>
    public MemoryUsage? Memory { get; init; }

    /// <summary>Load averages, or null if <c>/proc/loadavg</c> could not be read.</summary>
    public LoadAverage? Load { get; init; }

    /// <summary>Time since boot, or null if <c>/proc/uptime</c> could not be read.</summary>
    public TimeSpan? Uptime { get; init; }

    /// <summary>Usage per mounted filesystem.</summary>
    public ImmutableArray<MountUsage> Mounts { get; init; } = [];

    /// <summary>Throughput per network interface.</summary>
    public ImmutableArray<InterfaceThroughput> Interfaces { get; init; } = [];
}

/// <summary>Memory and swap usage, in bytes.</summary>
public sealed record MemoryUsage
{
    /// <summary>Total usable RAM.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    /// Memory available for new workloads without swapping, as the kernel estimates it.
    /// This is the number that matters — <c>MemFree</c> looks alarmingly low on a healthy
    /// machine because the page cache is doing its job.
    /// </summary>
    public required long AvailableBytes { get; init; }

    /// <summary>Memory used by the page cache and buffers, reclaimable under pressure.</summary>
    public required long CachedBytes { get; init; }

    /// <summary>Total swap space.</summary>
    public required long SwapTotalBytes { get; init; }

    /// <summary>Swap currently in use.</summary>
    public required long SwapUsedBytes { get; init; }

    /// <summary>Memory genuinely in use: total minus available.</summary>
    public long UsedBytes => TotalBytes - AvailableBytes;

    /// <summary>Used memory as a percentage of total.</summary>
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;

    /// <summary>Used swap as a percentage of total swap; zero when there is no swap.</summary>
    public double SwapUsedPercent => SwapTotalBytes > 0 ? SwapUsedBytes * 100.0 / SwapTotalBytes : 0;
}

/// <summary>Kernel load averages.</summary>
public sealed record LoadAverage
{
    /// <summary>One-minute load average.</summary>
    public required double OneMinute { get; init; }

    /// <summary>Five-minute load average.</summary>
    public required double FiveMinutes { get; init; }

    /// <summary>Fifteen-minute load average.</summary>
    public required double FifteenMinutes { get; init; }
}

/// <summary>Disk usage for one mounted filesystem.</summary>
public sealed record MountUsage
{
    /// <summary>Mount point, for example <c>/</c> or <c>/srv/media</c>.</summary>
    public required string MountPoint { get; init; }

    /// <summary>Backing device, for example <c>/dev/sda1</c>.</summary>
    public required string Device { get; init; }

    /// <summary>Filesystem type, for example <c>ext4</c>.</summary>
    public required string FileSystem { get; init; }

    /// <summary>Total size in bytes.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Free space in bytes.</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Used space in bytes.</summary>
    public long UsedBytes => TotalBytes - FreeBytes;

    /// <summary>Used space as a percentage of total.</summary>
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>Instantaneous throughput for one network interface.</summary>
public sealed record InterfaceThroughput
{
    /// <summary>Interface name, for example <c>eth0</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Receive rate in bytes per second, averaged over the sampling interval.</summary>
    public required double ReceiveBytesPerSecond { get; init; }

    /// <summary>Transmit rate in bytes per second, averaged over the sampling interval.</summary>
    public required double TransmitBytesPerSecond { get; init; }

    /// <summary>Total bytes received since boot.</summary>
    public required long TotalReceivedBytes { get; init; }

    /// <summary>Total bytes transmitted since boot.</summary>
    public required long TotalTransmittedBytes { get; init; }
}

/// <summary>Static identity of the machine, read once at startup.</summary>
public sealed record SystemIdentity
{
    /// <summary>Machine hostname.</summary>
    public required string HostName { get; init; }

    /// <summary>Pretty OS name from <c>/etc/os-release</c>, for example "Debian GNU/Linux 13 (trixie)".</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>Kernel release, for example "6.12.48+deb13-arm64".</summary>
    public required string KernelVersion { get; init; }

    /// <summary>
    /// CPU model name from <c>/proc/cpuinfo</c>, or null when the architecture does not report
    /// one (arm64 usually does not).
    /// </summary>
    public required string? CpuModel { get; init; }

    /// <summary>Number of logical CPUs.</summary>
    public required int CoreCount { get; init; }

    /// <summary>Process architecture, for example "Arm64".</summary>
    public required string Architecture { get; init; }
}
