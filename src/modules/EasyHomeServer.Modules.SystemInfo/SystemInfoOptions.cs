namespace EasyHomeServer.Modules.SystemInfo;

/// <summary>
/// Module settings, bound from the host's <c>Modules:systeminfo</c> configuration section.
/// </summary>
public sealed class SystemInfoOptions
{
    private const double MinimumIntervalSeconds = 0.5;
    private const double MaximumIntervalSeconds = 60;

    private double _sampleIntervalSeconds = 2;

    /// <summary>
    /// How often to sample, in seconds. Clamped to a sane range: below half a second the CPU
    /// deltas are noise, and above a minute the page stops feeling live.
    /// </summary>
    public double SampleIntervalSeconds
    {
        get => _sampleIntervalSeconds;
        set => _sampleIntervalSeconds = Math.Clamp(value, MinimumIntervalSeconds, MaximumIntervalSeconds);
    }

    /// <summary>
    /// Whether the page offers reboot and shutdown. Off makes the module read-only, which suits
    /// a box where power control belongs elsewhere.
    /// </summary>
    public bool AllowPowerControl { get; set; } = true;
}
