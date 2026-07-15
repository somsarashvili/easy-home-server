using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// Converts between compose YAML and the builder's model.
/// </summary>
/// <remarks>
/// <para>
/// The contract is that nothing is lost. Compose files in the wild use keys and shapes this
/// builder does not model, and quietly dropping a <c>healthcheck</c> because the form had no box
/// for it would be the worst kind of bug — invisible until the thing it protected failed. So
/// unknown keys are carried through verbatim, and a service whose <em>known</em> keys appear in
/// an unmodellable shape is marked advanced and shown read-only rather than half-parsed.
/// </para>
/// <para>
/// Comments are the exception, and cannot be helped: YAML comments are not part of the document
/// model, so a parse/serialise round trip drops them. Callers are expected to warn rather than
/// let that happen silently — see <see cref="HasComments"/>.
/// </para>
/// </remarks>
public static class ComposeYaml
{
    /// <summary>Result of parsing a compose file.</summary>
    public sealed record ParseResult
    {
        /// <summary>The parsed file, or null when it could not be parsed.</summary>
        public ComposeFile? File { get; init; }

        /// <summary>Why it could not be parsed, phrased for the operator.</summary>
        public string? Error { get; init; }

        /// <summary>True when parsing succeeded.</summary>
        public bool Succeeded => File is not null;
    }

    /// <summary>
    /// Whether the document contains comments, which a round trip through the builder would drop.
    /// </summary>
    /// <remarks>
    /// A deliberately simple scan. It looks for a <c>#</c> that starts a line or follows
    /// whitespace, which over-reports for a <c>#</c> inside a quoted string and under-reports
    /// nothing. Over-reporting costs an unnecessary warning; under-reporting costs someone's
    /// comments.
    /// </remarks>
    public static bool HasComments(string yaml)
    {
        if (string.IsNullOrEmpty(yaml))
        {
            return false;
        }

        foreach (var line in yaml.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("#"))
            {
                return true;
            }

            if (line.Contains(" #", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses compose YAML into the builder's model.</summary>
    public static ParseResult Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new ParseResult { File = new ComposeFile() };
        }

        try
        {
            var deserializer = new DeserializerBuilder().Build();
            var root = deserializer.Deserialize<object?>(yaml);

            if (root is not Dictionary<object, object?> map)
            {
                return new ParseResult { Error = "The file's top level must be a mapping of keys, such as 'services:'." };
            }

            var file = new ComposeFile();

            foreach (var (rawKey, value) in map)
            {
                var key = rawKey?.ToString() ?? string.Empty;

                switch (key)
                {
                    case "services":
                        ParseServices(value, file);

                        break;

                    case "networks":
                        ParseNetworks(value, file);

                        break;

                    default:
                        // volumes, configs, secrets, x-* extensions, and the obsolete version key.
                        file.UnknownKeys[key] = value;

                        break;
                }
            }

            return new ParseResult { File = file };
        }
        catch (YamlException ex)
        {
            // YamlDotNet's message carries the line and column, which is the useful part.
            return new ParseResult { Error = $"Line {ex.Start.Line}: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new ParseResult { Error = $"Could not read the compose file: {ex.Message}" };
        }
    }

    private static void ParseServices(object? node, ComposeFile file)
    {
        if (node is not Dictionary<object, object?> services)
        {
            return;
        }

        foreach (var (rawName, rawService) in services)
        {
            var name = rawName?.ToString() ?? string.Empty;

            if (rawService is not Dictionary<object, object?> service)
            {
                file.Services.Add(new ComposeServiceSpec
                {
                    Name = name,
                    IsAdvanced = true,
                    AdvancedReason = "This service is not a mapping.",
                    RawNode = rawService,
                });

                continue;
            }

            file.Services.Add(ParseService(name, service));
        }
    }

    private static ComposeServiceSpec ParseService(string name, Dictionary<object, object?> node)
    {
        var spec = new ComposeServiceSpec { Name = name, RawNode = node, Restart = string.Empty };

        foreach (var (rawKey, value) in node)
        {
            var key = rawKey?.ToString() ?? string.Empty;

            switch (key)
            {
                case "image":
                    spec.Image = value?.ToString() ?? string.Empty;

                    break;

                case "restart":
                    spec.Restart = value?.ToString() ?? string.Empty;

                    break;

                case "command":
                    // A list command (["sh", "-c", "x"]) has no faithful single-line form.
                    if (value is IList<object?> commandList)
                    {
                        return Advanced(spec, node, "Its command uses list syntax.");
                    }

                    spec.Command = value?.ToString() ?? string.Empty;

                    break;

                case "ports":
                    if (!TryParseStringList(value, out var ports))
                    {
                        return Advanced(spec, node, "Its ports use long syntax.");
                    }

                    spec.Ports = ports;

                    break;

                case "volumes":
                    if (!TryParseStringList(value, out var volumes))
                    {
                        return Advanced(spec, node, "Its volumes use long syntax.");
                    }

                    spec.Volumes = volumes;

                    break;

                case "environment":
                    if (!TryParseKeyValues(value, out var environment))
                    {
                        return Advanced(spec, node, "Its environment uses a shape the builder cannot show.");
                    }

                    spec.Environment = environment;

                    break;

                case "labels":
                    if (!TryParseKeyValues(value, out var labels))
                    {
                        return Advanced(spec, node, "Its labels use a shape the builder cannot show.");
                    }

                    foreach (var entry in labels)
                    {
                        var separator = entry.IndexOf('=');

                        if (separator > 0)
                        {
                            spec.Labels[entry[..separator]] = entry[(separator + 1)..];
                        }
                    }

                    break;

                case "networks":
                    if (!TryParseNetwork(value, out var network, out var address))
                    {
                        return Advanced(spec, node, "It attaches to more than one network, or uses options the builder cannot show.");
                    }

                    spec.Network = network;
                    spec.Ipv4Address = address;

                    break;

                default:
                    spec.UnknownKeys[key] = value;

                    break;
            }
        }

        return spec;
    }

    private static ComposeServiceSpec Advanced(ComposeServiceSpec spec, object node, string reason) => new()
    {
        Name = spec.Name,
        IsAdvanced = true,
        AdvancedReason = reason,
        RawNode = node,
    };

    /// <summary>Reads a list of plain strings, rejecting the long mapping syntax.</summary>
    private static bool TryParseStringList(object? node, out List<string> values)
    {
        values = [];

        if (node is null)
        {
            return true;
        }

        if (node is not IList<object?> list)
        {
            return false;
        }

        foreach (var item in list)
        {
            // A mapping here is compose's long syntax, which the builder does not model.
            if (item is Dictionary<object, object?>)
            {
                return false;
            }

            if (item is not null)
            {
                values.Add(Scalar(item));
            }
        }

        return true;
    }

    /// <summary>
    /// Reads environment or labels, which compose accepts as either a list of <c>KEY=value</c> or
    /// a mapping. Both are normalised to <c>KEY=value</c>.
    /// </summary>
    private static bool TryParseKeyValues(object? node, out List<string> values)
    {
        values = [];

        switch (node)
        {
            case null:
                return true;

            case Dictionary<object, object?> map:
                foreach (var (key, value) in map)
                {
                    values.Add($"{key}={Scalar(value)}");
                }

                return true;

            case IList<object?> list:
                foreach (var item in list)
                {
                    if (item is Dictionary<object, object?>)
                    {
                        return false;
                    }

                    if (item is not null)
                    {
                        values.Add(Scalar(item));
                    }
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Reads a service's networks. The builder models one network, optionally with a fixed
    /// address; anything richer is left to YAML.
    /// </summary>
    private static bool TryParseNetwork(object? node, out string network, out string address)
    {
        network = string.Empty;
        address = string.Empty;

        switch (node)
        {
            case null:
                return true;

            case IList<object?> list:
                if (list.Count == 0)
                {
                    return true;
                }

                if (list.Count > 1 || list[0] is Dictionary<object, object?>)
                {
                    return false;
                }

                network = Scalar(list[0]!);

                return true;

            case Dictionary<object, object?> map:
                if (map.Count != 1)
                {
                    return false;
                }

                var (name, options) = map.First();
                network = name?.ToString() ?? string.Empty;

                switch (options)
                {
                    case null:
                        return true;

                    case Dictionary<object, object?> optionMap:
                        foreach (var (key, value) in optionMap)
                        {
                            if (key?.ToString() == "ipv4_address")
                            {
                                address = Scalar(value);
                            }
                            else
                            {
                                // aliases, priority, ipv6_address: not modelled.
                                return false;
                            }
                        }

                        return true;

                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    private static void ParseNetworks(object? node, ComposeFile file)
    {
        if (node is not Dictionary<object, object?> networks)
        {
            return;
        }

        foreach (var (rawName, definition) in networks)
        {
            var name = rawName?.ToString() ?? string.Empty;

            if (definition is Dictionary<object, object?> map
                && map.Count == 1
                && map.TryGetValue("external", out var external))
            {
                file.ExternalNetworks[name] = external is true or "true";

                continue;
            }

            // A network defined inline (driver, ipam, …) is preserved rather than reduced to a
            // reference: the builder cannot express it and must not discard it.
            file.UnknownKeys[$"networks::{name}"] = definition;
        }
    }

    private static string Scalar(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Renders the model back to compose YAML.</summary>
    public static string Serialize(ComposeFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var root = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Top-level keys the builder does not model, minus the network entries folded in below.
        foreach (var (key, value) in file.UnknownKeys)
        {
            if (!key.StartsWith("networks::", StringComparison.Ordinal))
            {
                root[key] = value;
            }
        }

        var services = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var service in file.Services)
        {
            services[service.Name] = service.IsAdvanced ? service.RawNode : BuildService(service);
        }

        root["services"] = services;

        var networks = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (name, isExternal) in file.ExternalNetworks)
        {
            networks[name] = isExternal
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["external"] = true }
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        foreach (var (key, value) in file.UnknownKeys)
        {
            if (key.StartsWith("networks::", StringComparison.Ordinal))
            {
                networks[key["networks::".Length..]] = value;
            }
        }

        if (networks.Count > 0)
        {
            root["networks"] = networks;
        }

        // WithQuotingNecessaryStrings is not cosmetic: without it a label value of "true" is
        // emitted bare and reads back as a boolean, so easyhomeserver.avahi.enable stops being
        // the string the label contract expects. It quotes any scalar that would otherwise change
        // type on the way back in.
        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .WithQuotingNecessaryStrings()
            .WithIndentedSequences()
            .Build();

        return serializer.Serialize(root);
    }

    private static Dictionary<string, object?> BuildService(ComposeServiceSpec service)
    {
        // Insertion order is the emitted order, so this is also the reading order: what it is,
        // then how it runs, then how it is wired up.
        var node = new Dictionary<string, object?>(StringComparer.Ordinal) { ["image"] = service.Image };

        if (!string.IsNullOrWhiteSpace(service.Command))
        {
            node["command"] = service.Command;
        }

        if (!string.IsNullOrWhiteSpace(service.Restart))
        {
            node["restart"] = service.Restart;
        }

        if (service.Ports.Count > 0)
        {
            // Emitted bare. The old warning about YAML reading 22:22 as a sexagesimal number
            // applies to YAML 1.1; compose v2 parses with Go's YAML 1.2, and `compose config`
            // reads quoted and unquoted forms identically. Verified against compose 2.26.
            node["ports"] = service.Ports.Select(p => p.Trim()).ToList();
        }

        if (service.Volumes.Count > 0)
        {
            node["volumes"] = service.Volumes.Select(v => v.Trim()).ToList();
        }

        if (service.Environment.Count > 0)
        {
            node["environment"] = service.Environment.Select(e => e.Trim()).ToList();
        }

        if (service.Labels.Count > 0)
        {
            node["labels"] = service.Labels.ToDictionary(l => l.Key, l => (object?)l.Value, StringComparer.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(service.Network))
        {
            node["networks"] = string.IsNullOrWhiteSpace(service.Ipv4Address)
                ? new List<object?> { service.Network }
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [service.Network] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ipv4_address"] = service.Ipv4Address,
                    },
                };
        }

        foreach (var (key, value) in service.UnknownKeys)
        {
            node[key] = value;
        }

        return node;
    }
}
