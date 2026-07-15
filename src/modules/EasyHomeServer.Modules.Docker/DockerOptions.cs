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

    /// <summary>
    /// Directory holding compose projects created here, one subdirectory per project. Projects
    /// elsewhere on disk are still discovered from their containers' labels; they are just not
    /// editable from the UI.
    /// </summary>
    public string ComposeProjectsPath { get; set; } = "/srv/compose";

    /// <summary>
    /// How long to allow a compose operation before killing it. Minutes, not seconds: `up` pulls
    /// images, and a large image on a slow line legitimately takes a while. Killing it halfway
    /// leaves a partial pull and a project that never starts.
    /// </summary>
    public TimeSpan ComposeUpTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether the UI may create containers, volumes and compose projects. Separate from
    /// <see cref="AllowContainerControl"/>: allowing someone to restart a container is a smaller
    /// step than letting them run an arbitrary image.
    /// </summary>
    public bool AllowCreate { get; set; } = true;
}
