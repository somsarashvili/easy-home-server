using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// What the operator typed into the create-container form.
/// </summary>
/// <remarks>
/// <para>
/// Multi-value fields are free text, one entry per line, in exactly the syntax
/// <c>docker run</c> uses — <c>8080:80</c>, <c>KEY=value</c>, <c>/srv/data:/data:ro</c>. Anyone
/// creating containers already knows that syntax, and a form of separate host/container/protocol
/// dropdowns per port would be slower to use and would still need the same validation.
/// </para>
/// <para>
/// Values are validated here rather than trusted to docker, so the operator gets a specific
/// message next to the field instead of a wall of stderr after a failed run. This is not a
/// security boundary: <see cref="ISystemRunner"/> passes every value as its own argument and
/// never builds a shell command line, so a hostile value is inert regardless.
/// </para>
/// </remarks>
public sealed partial class ContainerSpec
{
    /// <summary>Container name. Required: an unnamed container gets a random one and is hard to find again.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Image reference, for example <c>nginx:alpine</c>.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Port mappings, one per line, <c>host:container[/proto]</c>.</summary>
    public string Ports { get; set; } = string.Empty;

    /// <summary>Volume mounts, one per line, <c>source:/target[:ro]</c>.</summary>
    public string Volumes { get; set; } = string.Empty;

    /// <summary>Environment variables, one per line, <c>KEY=value</c>.</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>Labels, one per line, <c>key=value</c>.</summary>
    public string Labels { get; set; } = string.Empty;

    /// <summary>Restart policy: no, always, unless-stopped, on-failure.</summary>
    public string RestartPolicy { get; set; } = "unless-stopped";

    /// <summary>Network to attach to. Empty means the default bridge.</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>Command override, replacing the image's CMD.</summary>
    public string Command { get; set; } = string.Empty;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ContainerNamePattern { get; }

    /// <summary>Port mapping: host[:container][/tcp|/udp], optionally prefixed with a bind address.</summary>
    [GeneratedRegex(@"^(?:(?<ip>[0-9a-fA-F:.]+):)?(?<host>\d{1,5}):(?<container>\d{1,5})(?:/(?<proto>tcp|udp))?$")]
    private static partial Regex PortPattern { get; }

    /// <summary>Returns the first validation error, or null when the spec is usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !ContainerNamePattern.IsMatch(Name))
        {
            return "Name must start with a letter or digit and contain only letters, digits, dots, dashes and underscores.";
        }

        if (string.IsNullOrWhiteSpace(Image))
        {
            return "Enter an image, for example nginx:alpine.";
        }

        foreach (var line in Lines(Ports))
        {
            var match = PortPattern.Match(line);

            if (!match.Success)
            {
                return $"Port mapping '{line}' is not valid. Use host:container, for example 8080:80.";
            }

            foreach (var group in new[] { "host", "container" })
            {
                var port = int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

                if (port is < 1 or > 65535)
                {
                    return $"Port {port} in '{line}' is out of range (1-65535).";
                }
            }
        }

        foreach (var line in Lines(Volumes))
        {
            // A named volume or an absolute host path, then an absolute path inside the
            // container. A relative target is the most common mistake and docker's own error for
            // it is obscure.
            var parts = line.Split(':');

            if (parts.Length is < 2 or > 3 || parts.Any(string.IsNullOrWhiteSpace))
            {
                return $"Volume '{line}' is not valid. Use source:/target, optionally with :ro.";
            }

            if (!parts[1].StartsWith('/'))
            {
                return $"Volume '{line}': the path inside the container must be absolute.";
            }

            if (parts.Length == 3 && parts[2] is not ("ro" or "rw"))
            {
                return $"Volume '{line}': the third part must be ro or rw.";
            }
        }

        foreach (var line in Lines(Environment))
        {
            if (!line.Contains('=', StringComparison.Ordinal) || line.StartsWith('='))
            {
                return $"Environment entry '{line}' is not valid. Use KEY=value.";
            }
        }

        foreach (var line in Lines(Labels))
        {
            if (!line.Contains('=', StringComparison.Ordinal) || line.StartsWith('='))
            {
                return $"Label '{line}' is not valid. Use key=value.";
            }
        }

        if (RestartPolicy is not ("no" or "always" or "unless-stopped" or "on-failure"))
        {
            return $"Restart policy '{RestartPolicy}' is not valid.";
        }

        return null;
    }

    /// <summary>Port mappings, one per <c>--publish</c>.</summary>
    public ImmutableArray<string> ParsePorts() => Lines(Ports);

    /// <summary>Volume mounts, one per <c>--volume</c>.</summary>
    public ImmutableArray<string> ParseVolumes() => Lines(Volumes);

    /// <summary>Environment variables, one per <c>--env</c>.</summary>
    public ImmutableArray<string> ParseEnvironment() => Lines(Environment);

    /// <summary>Labels, one per <c>--label</c>.</summary>
    public ImmutableArray<string> ParseLabels() => Lines(Labels);

    /// <summary>
    /// The command override, split on whitespace.
    /// </summary>
    /// <remarks>
    /// Naive splitting: quoting is not honoured, so <c>sh -c "a b"</c> becomes four arguments.
    /// Getting that right means implementing shell word-splitting, and a command needing it
    /// belongs in a compose file. The form says as much.
    /// </remarks>
    public ImmutableArray<string> ParseCommand() =>
        [.. Command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static ImmutableArray<string> Lines(string value) =>
    [
        .. value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0),
    ];
}
