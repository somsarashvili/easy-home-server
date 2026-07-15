using System.Collections.Immutable;
using System.Globalization;
using EasyHomeServer.Contracts.Docker;

namespace EasyHomeServer.Modules.Avahi.Avahi;

/// <summary>
/// Decides which containers get advertised, and as what, from their labels.
/// </summary>
/// <remarks>
/// <para>
/// Advertising is opt-in. Publishing every container with a mapped port would put a database
/// admin port, a metrics endpoint and a half-configured test container on the network the moment
/// this module is installed, and mDNS is a broadcast — every device on the LAN sees it. Opt-in
/// means nothing is announced that nobody asked to announce.
/// </para>
/// <para>
/// Labels are the mechanism because they are already in the Docker contract and they live with
/// the container: the declaration sits in the compose file next to the service it describes,
/// survives a recreate, and needs no state in this module.
/// </para>
/// </remarks>
public static class ContainerServiceMapper
{
    /// <summary>Set to <c>true</c> to advertise a container.</summary>
    public const string EnableLabel = "easyhomeserver.avahi.enable";

    /// <summary>Overrides the advertised name. Defaults to the container name.</summary>
    public const string NameLabel = "easyhomeserver.avahi.name";

    /// <summary>DNS-SD service type. Defaults to <c>_http._tcp</c>.</summary>
    public const string TypeLabel = "easyhomeserver.avahi.type";

    /// <summary>Which published host port to advertise, when the container publishes several.</summary>
    public const string PortLabel = "easyhomeserver.avahi.port";

    /// <summary>Path published as a TXT record, for <c>_http._tcp</c> browsers that use it.</summary>
    public const string PathLabel = "easyhomeserver.avahi.path";

    private const string DefaultServiceType = "_http._tcp";

    /// <summary>Turns an inventory into the advertisements it implies, with reasons for the rest.</summary>
    public static ImmutableArray<ContainerMapping> Map(ContainerInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return [.. inventory.Containers.Select(Map).OrderBy(m => m.ContainerName, StringComparer.Ordinal)];
    }

    private static ContainerMapping Map(ContainerInfo container)
    {
        ContainerMapping Skip(string reason) => new()
        {
            ContainerName = container.Name,
            Service = null,
            SkipReason = reason,
            IsOptedIn = IsOptedIn(container),
        };

        if (!IsOptedIn(container))
        {
            return Skip($"Not opted in. Add the label {EnableLabel}=true to advertise it.");
        }

        if (!container.IsRunning)
        {
            // Advertising a stopped container would point browsers at a closed port.
            return Skip("Opted in, but not running.");
        }

        // Only ports reachable from another machine: a port bound to 127.0.0.1 exists only on
        // the server, so announcing it to the LAN would send everyone to a closed door.
        var candidates = container.Ports
            .Where(p => p.Protocol == "tcp" && p.IsReachableFromNetwork)
            .ToImmutableArray();

        if (candidates.Length == 0)
        {
            return Skip(
                container.Ports.Length == 0
                    ? "Opted in, but publishes no ports."
                    : "Opted in, but publishes no TCP port reachable from the network.");
        }

        var port = SelectPort(container, candidates);

        if (port is null)
        {
            var requested = container.Labels[PortLabel];

            return Skip($"Label {PortLabel}={requested} does not match any published port.");
        }

        var txt = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        if (container.Labels.TryGetValue(PathLabel, out var path) && path.Length > 0)
        {
            txt["path"] = path;
        }

        return new ContainerMapping
        {
            ContainerName = container.Name,
            IsOptedIn = true,
            SkipReason = null,
            Service = new ServiceDefinition
            {
                Key = container.Name,
                DisplayName = container.Labels.TryGetValue(NameLabel, out var name) && name.Length > 0
                    ? name

                    // %h is substituted by avahi with the hostname, so the same container on two
                    // machines does not collide on the network.
                    : $"{container.Name} on %h",
                ServiceType = container.Labels.TryGetValue(TypeLabel, out var type) && type.Length > 0
                    ? type
                    : DefaultServiceType,
                Port = port.HostPort,
                TxtRecords = txt.ToImmutable(),
                Origin = ServiceOrigin.Container,
                ContainerName = container.Name,
            },
        };
    }

    private static bool IsOptedIn(ContainerInfo container) =>
        container.Labels.TryGetValue(EnableLabel, out var value)
        && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    /// <summary>
    /// Picks the port to advertise. With one candidate it is unambiguous; with several the
    /// container must say which, because guessing would announce the wrong one silently.
    /// </summary>
    private static PublishedPort? SelectPort(ContainerInfo container, ImmutableArray<PublishedPort> candidates)
    {
        if (container.Labels.TryGetValue(PortLabel, out var requested)
            && int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted))
        {
            return candidates.FirstOrDefault(p => p.HostPort == wanted || p.ContainerPort == wanted);
        }

        return candidates.OrderBy(p => p.HostPort).First();
    }
}

/// <summary>A container and what, if anything, it should advertise.</summary>
public sealed record ContainerMapping
{
    /// <summary>Container name.</summary>
    public required string ContainerName { get; init; }

    /// <summary>The advertisement, or null when the container is not advertised.</summary>
    public required ServiceDefinition? Service { get; init; }

    /// <summary>Why it is not advertised, phrased for the operator. Null when it is.</summary>
    public required string? SkipReason { get; init; }

    /// <summary>Whether the container carries the opt-in label at all.</summary>
    public required bool IsOptedIn { get; init; }
}
