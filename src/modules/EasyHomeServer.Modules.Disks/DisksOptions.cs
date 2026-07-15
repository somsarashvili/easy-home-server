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
