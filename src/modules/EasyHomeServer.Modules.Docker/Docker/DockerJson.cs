using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// Projects <c>docker inspect</c> output into the module's own models.
/// </summary>
/// <remarks>
/// Hand-written against <see cref="JsonElement"/> rather than deserialised into generated
/// types. The inspect schema is large, deeply nested, varies by daemon version and is mostly
/// irrelevant here; mapping the dozen fields actually displayed keeps the module tolerant of a
/// daemon upgrade adding or moving anything else. Every lookup is optional by construction — a
/// missing field yields a null or a default, never an exception.
/// </remarks>
internal static class DockerJson
{
    public static DockerContainer? ParseContainer(JsonElement element)
    {
        var id = GetString(element, "Id");

        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var state = element.TryGetProperty("State", out var stateElement) ? stateElement : default;
        var config = element.TryGetProperty("Config", out var configElement) ? configElement : default;
        var hostConfig = element.TryGetProperty("HostConfig", out var hostElement) ? hostElement : default;
        var networkSettings = element.TryGetProperty("NetworkSettings", out var netElement) ? netElement : default;

        return new DockerContainer
        {
            Id = id,

            // The API returns names with a leading slash, a historical artefact of the days
            // when containers could be linked as /parent/child.
            Name = GetString(element, "Name")?.TrimStart('/') ?? id[..Math.Min(12, id.Length)],
            Image = GetString(config, "Image") ?? GetString(element, "Image") ?? "(unknown)",
            State = ParseState(GetString(state, "Status")),
            Health = state.ValueKind == JsonValueKind.Object
                     && state.TryGetProperty("Health", out var health)
                ? GetString(health, "Status")
                : null,
            CreatedAt = GetDate(element, "Created"),
            StartedAt = GetDate(state, "StartedAt"),
            FinishedAt = GetDate(state, "FinishedAt"),
            ExitCode = GetInt(state, "ExitCode"),
            RestartPolicy = hostConfig.ValueKind == JsonValueKind.Object
                            && hostConfig.TryGetProperty("RestartPolicy", out var policy)
                ? GetString(policy, "Name") ?? "no"
                : "no",
            Ports = ParsePorts(networkSettings),
            NetworkAttachments = ParseNetworkAttachments(networkSettings),
            ExposedPorts = ParseExposedPorts(config),
            VolumeMounts = ParseVolumeMounts(element),
            Labels = ParseLabels(config),
        };
    }

    /// <summary>
    /// Reads the ports the image declares with EXPOSE, from <c>Config.ExposedPorts</c>, which is
    /// keyed like <c>"80/tcp"</c>. UDP is skipped: nothing here advertises or links a UDP port.
    /// </summary>
    private static ImmutableArray<int> ParseExposedPorts(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("ExposedPorts", out var exposed)
            || exposed.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<int>();

        foreach (var entry in exposed.EnumerateObject())
        {
            var slash = entry.Name.IndexOf('/');
            var portText = slash > 0 ? entry.Name[..slash] : entry.Name;
            var protocol = slash > 0 ? entry.Name[(slash + 1)..] : "tcp";

            if (protocol == "tcp"
                && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            {
                builder.Add(port);
            }
        }

        return [.. builder.Distinct().Order()];
    }

    /// <summary>
    /// Reads the named volumes from <c>Mounts</c>. Entries with <c>Type</c> of <c>bind</c> or
    /// <c>tmpfs</c> are skipped: only a <c>volume</c> mount refers to something the volumes tab
    /// lists, and counting a bind mount would attribute usage to a volume that does not exist.
    /// </summary>
    private static ImmutableArray<string> ParseVolumeMounts(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("Mounts", out var mounts)
            || mounts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var mount in mounts.EnumerateArray())
        {
            if (!string.Equals(GetString(mount, "Type"), "volume", StringComparison.Ordinal))
            {
                continue;
            }

            if (GetString(mount, "Name") is { Length: > 0 } name)
            {
                builder.Add(name);
            }
        }

        return builder.ToImmutable();
    }

    private static ContainerState ParseState(string? status) => status?.ToLowerInvariant() switch
    {
        "created" => ContainerState.Created,
        "running" => ContainerState.Running,
        "paused" => ContainerState.Paused,
        "restarting" => ContainerState.Restarting,
        "removing" => ContainerState.Removing,
        "exited" => ContainerState.Exited,
        "dead" => ContainerState.Dead,
        _ => ContainerState.Unknown,
    };

    /// <summary>
    /// Reads <c>NetworkSettings.Ports</c>, which maps "80/tcp" to an array of host bindings, or
    /// to null when the port is exposed but not published. Only published ports are returned —
    /// an exposed-but-unpublished port is unreachable and would be a misleading thing to show.
    /// </summary>
    private static ImmutableArray<PortBinding> ParsePorts(JsonElement networkSettings)
    {
        if (networkSettings.ValueKind != JsonValueKind.Object
            || !networkSettings.TryGetProperty("Ports", out var ports)
            || ports.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<PortBinding>();

        foreach (var entry in ports.EnumerateObject())
        {
            // Null bindings mean EXPOSE without -p.
            if (entry.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var slash = entry.Name.IndexOf('/');
            var portText = slash > 0 ? entry.Name[..slash] : entry.Name;
            var protocol = slash > 0 ? entry.Name[(slash + 1)..] : "tcp";

            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var containerPort))
            {
                continue;
            }

            foreach (var binding in entry.Value.EnumerateArray())
            {
                var hostPortText = GetString(binding, "HostPort");

                if (!int.TryParse(hostPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPort))
                {
                    continue;
                }

                builder.Add(new PortBinding
                {
                    ContainerPort = containerPort,
                    HostPort = hostPort,
                    HostIp = GetString(binding, "HostIp") is { Length: > 0 } ip ? ip : "0.0.0.0",
                    Protocol = protocol,
                });
            }
        }

        // A port published on both IPv4 and IPv6 appears twice; the distinction is noise here.
        return [.. builder
            .DistinctBy(p => (p.ContainerPort, p.HostPort, p.Protocol))
            .OrderBy(p => p.HostPort)];
    }

    /// <summary>
    /// Reads <c>NetworkSettings.Networks</c>, which maps each attached network's name to the
    /// address and MAC the container holds on it. The address is what makes a macvlan container
    /// reachable in its own right, so it is carried rather than only the network's name.
    /// </summary>
    private static ImmutableArray<NetworkAttachment> ParseNetworkAttachments(JsonElement networkSettings)
    {
        if (networkSettings.ValueKind != JsonValueKind.Object
            || !networkSettings.TryGetProperty("Networks", out var networks)
            || networks.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<NetworkAttachment>();

        foreach (var network in networks.EnumerateObject())
        {
            builder.Add(new NetworkAttachment
            {
                NetworkName = network.Name,

                // Empty for drivers that assign no address of their own, such as host.
                IpAddress = GetString(network.Value, "IPAddress") ?? string.Empty,
                MacAddress = GetString(network.Value, "MacAddress"),
            });
        }

        return [.. builder.OrderBy(a => a.NetworkName, StringComparer.Ordinal)];
    }

    /// <summary>Reads a string-to-string object, tolerating the null Docker emits for an empty one.</summary>
    private static ImmutableDictionary<string, string> ParseStringMap(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var map)
            || map.ValueKind != JsonValueKind.Object)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                builder[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, string> ParseLabels(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("Labels", out var labels)
            || labels.ValueKind != JsonValueKind.Object)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var label in labels.EnumerateObject())
        {
            if (label.Value.ValueKind == JsonValueKind.String)
            {
                builder[label.Name] = label.Value.GetString() ?? string.Empty;
            }
        }

        return builder.ToImmutable();
    }

    public static DockerImage? ParseImage(JsonElement element)
    {
        var id = GetString(element, "Id");

        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var tags = element.TryGetProperty("RepoTags", out var repoTags) && repoTags.ValueKind == JsonValueKind.Array
            ? repoTags.EnumerateArray()
                .Select(t => t.GetString())
                .Where(t => !string.IsNullOrEmpty(t) && t != "<none>:<none>")
                .Select(t => t!)
                .ToImmutableArray()
            : [];

        return new DockerImage
        {
            Id = id,
            Tags = tags,
            SizeBytes = GetLong(element, "Size") ?? 0,
            CreatedAt = GetDate(element, "Created"),
        };
    }

    public static DockerVolume? ParseVolume(JsonElement element)
    {
        var name = GetString(element, "Name");

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new DockerVolume
        {
            Name = name,
            Driver = GetString(element, "Driver") ?? "local",
            MountPoint = GetString(element, "Mountpoint") ?? string.Empty,

            // Carries the device= of a bound volume, which is the only place the real path
            // appears — Mountpoint reports Docker's own directory either way.
            Options = ParseStringMap(element, "Options"),
            CreatedAt = GetDate(element, "CreatedAt"),
        };
    }

    public static DockerNetwork? ParseNetwork(JsonElement element)
    {
        var id = GetString(element, "Id");
        var name = GetString(element, "Name");

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var subnets = ImmutableArray<string>.Empty;

        if (element.TryGetProperty("IPAM", out var ipam)
            && ipam.ValueKind == JsonValueKind.Object
            && ipam.TryGetProperty("Config", out var config)
            && config.ValueKind == JsonValueKind.Array)
        {
            subnets = [.. config
                .EnumerateArray()
                .Select(c => GetString(c, "Subnet"))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)];
        }

        return new DockerNetwork
        {
            Id = id,
            Name = name,
            Driver = GetString(element, "Driver") ?? "bridge",
            Scope = GetString(element, "Scope") ?? "local",
            Subnets = subnets,
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt64(out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Reads a timestamp. Docker uses RFC3339 with nanosecond precision for containers, but a
    /// Unix epoch number for image Created — hence both branches.
    /// </summary>
    private static DateTimeOffset? GetDate(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // A container that has never run reports the zero timestamp rather than omitting the field.
        if (text.StartsWith("0001-01-01", StringComparison.Ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }
}
