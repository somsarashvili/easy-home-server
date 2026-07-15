namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// A compose file as the builder understands it.
/// </summary>
/// <remarks>
/// Deliberately not a model of the whole Compose specification, which is enormous and still
/// growing. It models what the builder offers, and carries everything else through untouched in
/// <see cref="UnknownKeys"/> — the guarantee being that opening a file in the builder and saving
/// it never silently loses something the form had no box for.
/// </remarks>
public sealed class ComposeFile
{
    /// <summary>Services, in file order.</summary>
    public List<ComposeServiceSpec> Services { get; set; } = [];

    /// <summary>
    /// Networks the services attach to, and whether each is external.
    /// </summary>
    /// <remarks>
    /// Networks are referenced, not defined, when they come from the Networks tab: that is where
    /// a macvlan gets its host address reserved and its shim registered, neither of which compose
    /// can do.
    /// </remarks>
    public Dictionary<string, bool> ExternalNetworks { get; set; } = [];

    /// <summary>
    /// Top-level keys the builder does not model — <c>volumes:</c>, <c>configs:</c>,
    /// <c>secrets:</c>, <c>x-</c> extensions — preserved verbatim.
    /// </summary>
    public Dictionary<string, object?> UnknownKeys { get; set; } = [];
}

/// <summary>One service in a compose file.</summary>
public sealed class ComposeServiceSpec
{
    /// <summary>Service name, the key under <c>services:</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Image reference.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Restart policy: no, always, unless-stopped, on-failure.</summary>
    public string Restart { get; set; } = "unless-stopped";

    /// <summary>Command override, as written.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Published ports in short syntax, one per entry: <c>8080:80</c>.</summary>
    public List<string> Ports { get; set; } = [];

    /// <summary>Volume mounts in short syntax: <c>data:/var/lib</c>.</summary>
    public List<string> Volumes { get; set; } = [];

    /// <summary>Environment variables as <c>KEY=value</c>.</summary>
    public List<string> Environment { get; set; } = [];

    /// <summary>Labels.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>Network to attach to. Empty means compose's default network for the project.</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>
    /// Fixed address on <see cref="Network"/>. Only meaningful on a macvlan or ipvlan, where the
    /// container is a machine on the LAN and a stable address is the point.
    /// </summary>
    public string Ipv4Address { get; set; } = string.Empty;

    /// <summary>
    /// Keys of this service the builder does not model — <c>healthcheck</c>, <c>depends_on</c>,
    /// <c>deploy</c> — preserved verbatim.
    /// </summary>
    public Dictionary<string, object?> UnknownKeys { get; set; } = [];

    /// <summary>
    /// True when a key the builder <em>does</em> model appeared in a shape it cannot represent —
    /// ports in long syntax, for example.
    /// </summary>
    /// <remarks>
    /// Such a service is shown read-only rather than half-edited. The alternative is worse:
    /// rendering an empty Ports box for a service that has ports, and erasing them on save.
    /// </remarks>
    public bool IsAdvanced { get; set; }

    /// <summary>Why the service is advanced, for display. Null when it is not.</summary>
    public string? AdvancedReason { get; set; }

    /// <summary>The service's original YAML, kept verbatim for round-tripping an advanced service.</summary>
    public object? RawNode { get; set; }
}
