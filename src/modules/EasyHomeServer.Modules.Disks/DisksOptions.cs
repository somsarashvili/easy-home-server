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
    /// Where this module may create a mount point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anywhere else must already exist, so a mistyped path cannot have a directory conjured for it
    /// and a filesystem mounted over the result. The guard is about creating directories, not about
    /// mounting: an existing path anywhere may still be used.
    /// </para>
    /// <para>
    /// All three are the places the filesystem hierarchy sets aside for this, and creating a
    /// directory under any of them is what they are for. <c>/mnt</c> matters most in practice —
    /// it is where hand-built arrays put their disks, so leaving it out meant refusing to create
    /// the very directories a pool is assembled from.
    /// </para>
    /// <para>
    /// Settable, not a get-only collection: the binder populates one of those by adding to it, so
    /// configuring this would extend the defaults rather than replace them, and there would be no
    /// way to narrow the list.
    /// </para>
    /// </remarks>
    public string[] MountRoots { get; set; } = ["/srv", "/mnt", "/media"];

    /// <summary>
    /// Where a suggested mount point goes by default. The first of <see cref="MountRoots"/>.
    /// </summary>
    public string DefaultMountRoot => MountRoots.Length > 0 ? MountRoots[0] : "/srv";

    /// <summary>The fstab to manage. Configurable so it can be pointed elsewhere for a test.</summary>
    public string FstabPath { get; set; } = "/etc/fstab";

    /// <summary>Where snapraid.conf lives. Its absence is how this module decides there is no array.</summary>
    public string SnapRaidConfigPath { get; set; } = "/etc/snapraid.conf";

    /// <summary>
    /// Where the copy of SnapRAID's content file that does not live on a data disk is kept.
    /// </summary>
    public string SnapRaidContentRoot { get; set; } = "/var/snapraid";

    /// <summary>
    /// Whether the UI may create pools and arrays. Separate from <see cref="AllowFormat"/>: this
    /// writes configuration and destroys nothing, so it is not the same risk.
    /// </summary>
    public bool AllowPoolControl { get; set; } = true;

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
