using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>One container, projected from <c>docker inspect</c>'s full API object.</summary>
public sealed record DockerContainer
{
    /// <summary>Full 64-character container id.</summary>
    public required string Id { get; init; }

    /// <summary>First 12 characters of <see cref="Id"/>, as the CLI displays it.</summary>
    public string ShortId => Id.Length >= 12 ? Id[..12] : Id;

    /// <summary>Container name, without the leading slash the API returns.</summary>
    public required string Name { get; init; }

    /// <summary>Image reference the container was created from.</summary>
    public required string Image { get; init; }

    /// <summary>Lifecycle state: running, exited, paused, created, restarting, removing, dead.</summary>
    public required ContainerState State { get; init; }

    /// <summary>Health from the image's HEALTHCHECK, or null when it declares none.</summary>
    public string? Health { get; init; }

    /// <summary>When the container was created.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the current run started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the last run ended.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Exit code of the last run; meaningful only once exited.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Restart policy name: no, always, unless-stopped, on-failure.</summary>
    public string RestartPolicy { get; init; } = "no";

    /// <summary>Published port bindings. Unpublished container ports are omitted.</summary>
    public ImmutableArray<PortBinding> Ports { get; init; } = [];

    /// <summary>Networks the container is attached to, with the address it holds on each.</summary>
    public ImmutableArray<NetworkAttachment> NetworkAttachments { get; init; } = [];

    /// <summary>Attached network names.</summary>
    public ImmutableArray<string> Networks => [.. NetworkAttachments.Select(a => a.NetworkName)];

    /// <summary>
    /// The container's own address on the LAN, when attached to a macvlan or ipvlan network.
    /// Null for the usual bridged case.
    /// </summary>
    /// <remarks>
    /// Set by the poller rather than parsed from inspect: deciding this needs the *network's*
    /// driver, which a container's own inspect output does not carry. The poller has both.
    /// </remarks>
    public string? LanAddress { get; init; }

    /// <summary>Ports the image declares with EXPOSE, published or not.</summary>
    public ImmutableArray<int> ExposedPorts { get; init; } = [];

    /// <summary>
    /// Names of named volumes this container mounts. Bind mounts are excluded — they are host
    /// paths, not volumes, and have no entry on the volumes tab.
    /// </summary>
    public ImmutableArray<string> VolumeMounts { get; init; } = [];

    /// <summary>Container labels.</summary>
    public ImmutableDictionary<string, string> Labels { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Compose project this container belongs to, from the
    /// <c>com.docker.compose.project</c> label, or null if not compose-managed.
    /// </summary>
    public string? ComposeProject =>
        Labels.TryGetValue("com.docker.compose.project", out var project) ? project : null;

    /// <summary>True when the container is doing work right now.</summary>
    public bool IsRunning => State is ContainerState.Running;

    /// <summary>
    /// How long the current run has lasted, or null when not running. Computed on read rather
    /// than sampled, so it stays correct between polls.
    /// </summary>
    public TimeSpan? Uptime => IsRunning && StartedAt is { } started
        ? DateTimeOffset.UtcNow - started
        : null;
}

/// <summary>Container lifecycle state. Mirrors the API's <c>State.Status</c>.</summary>
public enum ContainerState
{
    /// <summary>State string was absent or not recognised.</summary>
    Unknown,

    /// <summary>Created but never started.</summary>
    Created,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Paused.</summary>
    Paused,

    /// <summary>Restarting.</summary>
    Restarting,

    /// <summary>Removal in progress.</summary>
    Removing,

    /// <summary>Stopped, with an exit code.</summary>
    Exited,

    /// <summary>The daemon could not stop it cleanly.</summary>
    Dead,
}

/// <summary>A container's attachment to one network.</summary>
public sealed record NetworkAttachment
{
    /// <summary>Network name.</summary>
    public required string NetworkName { get; init; }

    /// <summary>Address the container holds on this network. Empty for drivers that assign none, such as host.</summary>
    public required string IpAddress { get; init; }

    /// <summary>The container's MAC on this network.</summary>
    public string? MacAddress { get; init; }
}

/// <summary>A published port mapping from host to container.</summary>
public sealed record PortBinding
{
    /// <summary>Port inside the container.</summary>
    public required int ContainerPort { get; init; }

    /// <summary>Port on the host.</summary>
    public required int HostPort { get; init; }

    /// <summary>Host interface the port is bound to; <c>0.0.0.0</c> means every interface.</summary>
    public required string HostIp { get; init; }

    /// <summary>tcp or udp.</summary>
    public required string Protocol { get; init; }
}

/// <summary>One image.</summary>
public sealed record DockerImage
{
    /// <summary>Full image id, including the <c>sha256:</c> prefix.</summary>
    public required string Id { get; init; }

    /// <summary>Twelve significant characters of the digest, as the CLI displays it.</summary>
    public string ShortId
    {
        get
        {
            var digest = Id.StartsWith("sha256:", StringComparison.Ordinal) ? Id[7..] : Id;

            return digest.Length >= 12 ? digest[..12] : digest;
        }
    }

    /// <summary>repo:tag references. Empty for a dangling image.</summary>
    public ImmutableArray<string> Tags { get; init; } = [];

    /// <summary>Best display name: first tag, or the short id for a dangling image.</summary>
    public string DisplayName => Tags.Length > 0 ? Tags[0] : $"<none>:<none> ({ShortId})";

    /// <summary>On-disk size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>When the image was built.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>True when the image has no tags — a leftover of a rebuild, and safe to prune.</summary>
    public bool IsDangling => Tags.Length == 0;

    /// <summary>Number of containers using this image. Set by the poller, not by inspect.</summary>
    public int UsedByContainers { get; init; }
}

/// <summary>One volume.</summary>
public sealed record DockerVolume
{
    /// <summary>Volume name.</summary>
    public required string Name { get; init; }

    /// <summary>Storage driver, usually <c>local</c>.</summary>
    public required string Driver { get; init; }

    /// <summary>Path on the host where the volume's data lives.</summary>
    public required string MountPoint { get; init; }

    /// <summary>When the volume was created.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Number of containers using this volume. Set by the poller.</summary>
    public int UsedByContainers { get; init; }

    /// <summary>True when nothing references it — safe to prune, and the usual source of disk creep.</summary>
    public bool IsOrphaned => UsedByContainers == 0;
}

/// <summary>One network.</summary>
public sealed record DockerNetwork
{
    /// <summary>Network id.</summary>
    public required string Id { get; init; }

    /// <summary>Network name.</summary>
    public required string Name { get; init; }

    /// <summary>Driver: bridge, host, none, macvlan, overlay.</summary>
    public required string Driver { get; init; }

    /// <summary>Scope, usually <c>local</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>Configured subnets.</summary>
    public ImmutableArray<string> Subnets { get; init; } = [];

    /// <summary>Number of containers attached. Set by the poller.</summary>
    public int UsedByContainers { get; init; }

    /// <summary>Docker's own networks, which cannot be removed.</summary>
    public bool IsBuiltIn => Name is "bridge" or "host" or "none";
}
