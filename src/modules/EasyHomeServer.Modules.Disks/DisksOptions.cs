namespace EasyHomeServer.Modules.Disks;

/// <summary>Module settings, bound from the host's <c>Modules:disks</c> configuration section.</summary>
public sealed class DisksOptions
{
    private const double MinimumIntervalSeconds = 2;
    private const double MaximumIntervalSeconds = 300;

    private double _pollIntervalSeconds = 10;

    /// <summary>
    /// How often to re-read the block devices, in seconds. Slower than the other modules on
    /// purpose: disks appear and disappear when someone plugs something in, not continuously, and
    /// this exists to notice that rather than to watch a graph.
    /// </summary>
    public double PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set => _pollIntervalSeconds = Math.Clamp(value, MinimumIntervalSeconds, MaximumIntervalSeconds);
    }

    /// <summary>
    /// Where this module creates mount points. Anywhere outside it must already exist, so a typo
    /// cannot mount a filesystem over part of the running system.
    /// </summary>
    public string MountRoot { get; set; } = "/srv";

    /// <summary>The fstab to manage. Configurable so it can be pointed elsewhere for a test.</summary>
    public string FstabPath { get; set; } = "/etc/fstab";

    /// <summary>Where snapraid.conf lives. Its absence is how this module decides there is no array.</summary>
    public string SnapRaidConfigPath { get; set; } = "/etc/snapraid.conf";

    private double _snapRaidPollIntervalSeconds = 600;

    /// <summary>
    /// How often to re-read the SnapRAID array, in seconds.
    /// </summary>
    /// <remarks>
    /// Far slower than the disk poll, because <c>snapraid status</c> is not a cheap read: it loads
    /// the whole content file, which on a real array is a few hundred MiB, and it takes the lock
    /// that a sync needs. Running it every ten seconds would fight the hourly cron sync for the
    /// array and read a large file continuously, to watch numbers that move once an hour.
    /// </remarks>
    public double SnapRaidPollIntervalSeconds
    {
        get => _snapRaidPollIntervalSeconds;
        set => _snapRaidPollIntervalSeconds = Math.Clamp(value, 60, 86400);
    }

    /// <summary>
    /// Whether the UI may mount, unmount and edit fstab.
    /// </summary>
    public bool AllowMountControl { get; set; } = true;

    /// <summary>
    /// Whether the UI may prepare a disk, destroying everything on it. Separate from mounting,
    /// which is reversible; this is not.
    /// </summary>
    public bool AllowFormat { get; set; } = true;
}
