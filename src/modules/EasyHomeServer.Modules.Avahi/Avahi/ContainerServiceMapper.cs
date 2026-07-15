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

        // A container with its own address on the LAN (macvlan/ipvlan) is a different shape of
        // problem: it publishes nothing to the host, so the published-port path below finds
        // nothing to advertise, and pointing browsers at the host would be wrong anyway — the
        // host cannot even reach it.
        if (container.HasOwnLanAddress)
        {
            return MapLanAddressed(container, Skip);
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

    /// <summary>
    /// Maps a container that holds its own address on the LAN, giving it a hostname of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is <c>jellyfin.local</c> resolving straight to the container, on the port the
    /// image actually listens on — no host, no port juggling. That needs two things avahi keeps
    /// apart: a static host record mapping the name to the address, and a service whose
    /// <c>host-name</c> names it.
    /// </para>
    /// <para>
    /// The port comes from EXPOSE rather than a published port, since there is no published port.
    /// Where the image exposes several, the container must say which — guessing would advertise
    /// the wrong one and look like a broken service.
    /// </para>
    /// </remarks>
    private static ContainerMapping MapLanAddressed(ContainerInfo container, Func<string, ContainerMapping> skip)
    {
        var port = SelectLanPort(container);

        if (port is null)
        {
            if (container.ExposedPorts.Length == 0)
            {
                return skip(
                    $"Has its own address ({container.LanAddress}) but the image exposes no port. "
                    + $"Add the label {PortLabel} to say which port to advertise.");
            }

            return container.Labels.ContainsKey(PortLabel)
                ? skip($"Label {PortLabel}={container.Labels[PortLabel]} is not among the exposed ports "
                       + $"({string.Join(", ", container.ExposedPorts)}).")
                : skip($"Has its own address ({container.LanAddress}) and exposes several ports "
                       + $"({string.Join(", ", container.ExposedPorts)}). Add {PortLabel} to choose one.");
        }

        var hostName = $"{SanitiseHostName(container.Name)}.local";

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

                // No "on %h" here: the service is not on this host, it is its own machine as far
                // as the network is concerned.
                DisplayName = container.Labels.TryGetValue(NameLabel, out var name) && name.Length > 0
                    ? name
                    : container.Name,
                ServiceType = container.Labels.TryGetValue(TypeLabel, out var type) && type.Length > 0
                    ? type
                    : DefaultServiceType,
                Port = port.Value,
                HostName = hostName,
                HostAddress = container.LanAddress,
                TxtRecords = txt.ToImmutable(),
                Origin = ServiceOrigin.Container,
                ContainerName = container.Name,
            },
        };
    }

    private static int? SelectLanPort(ContainerInfo container)
    {
        if (container.Labels.TryGetValue(PortLabel, out var requested)
            && int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted))
        {
            // An explicit port is honoured even when the image exposes nothing: EXPOSE is only a
            // declaration, and plenty of images listen on ports they never declare.
            return container.ExposedPorts.Length == 0 || container.ExposedPorts.Contains(wanted) ? wanted : null;
        }

        return container.ExposedPorts.Length == 1 ? container.ExposedPorts[0] : null;
    }

    /// <summary>
    /// Reduces a container name to a legal DNS label.
    /// </summary>
    /// <remarks>
    /// Container names allow underscores and dots; a hostname label allows neither. Compose names
    /// containers like <c>stack-web-1</c>, which is already fine, but <c>my_app</c> would produce
    /// a name avahi refuses to publish.
    /// </remarks>
    private static string SanitiseHostName(string name)
    {
        var cleaned = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')])
            .Trim('-');

        while (cleaned.Contains("--", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        }

        // A DNS label is capped at 63 characters.
        return cleaned.Length switch
        {
            0 => "container",
            > 63 => cleaned[..63].TrimEnd('-'),
            _ => cleaned,
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
