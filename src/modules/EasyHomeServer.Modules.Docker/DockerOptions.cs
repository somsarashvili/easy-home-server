namespace EasyHomeServer.Modules.Docker;

/// <summary>Module settings, bound from the host's <c>Modules:docker</c> configuration section.</summary>
public sealed class DockerOptions
{
    private const double MinimumIntervalSeconds = 1;
    private const double MaximumIntervalSeconds = 300;

    private double _pollIntervalSeconds = 3;

    /// <summary>
    /// How often to poll the daemon, in seconds. Slower than SystemInfo's sampler on purpose:
    /// each poll spawns several <c>docker inspect</c> calls, and containers do not change often
    /// enough to justify paying that every two seconds.
    /// </summary>
    public double PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set => _pollIntervalSeconds = Math.Clamp(value, MinimumIntervalSeconds, MaximumIntervalSeconds);
    }

    /// <summary>
    /// Whether the page may start, stop and remove things. Off makes the module read-only,
    /// which suits a box where containers are owned by compose files under version control.
    /// </summary>
    public bool AllowContainerControl { get; set; } = true;

    /// <summary>Lines of log tail to fetch. Bounded to keep a chatty container from flooding the browser.</summary>
    public int LogTailLines { get; set; } = 200;
}
