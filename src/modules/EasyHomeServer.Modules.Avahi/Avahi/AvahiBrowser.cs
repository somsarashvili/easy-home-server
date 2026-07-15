using System.Collections.Immutable;
using System.Globalization;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Avahi.Avahi;

/// <summary>
/// Discovers what is being advertised on the LAN, via <c>avahi-browse</c>.
/// </summary>
/// <remarks>
/// This is the answer to "is my service actually visible?", which is the only question that
/// matters after writing a service file — and one the file itself cannot answer. It browses the
/// network the way another machine would, so what it lists is what a laptop on the same LAN sees.
/// </remarks>
public sealed class AvahiBrowser(ISystemRunner systemRunner, ILogger<AvahiBrowser> logger)
{
    /// <summary>Result of probing for the avahi daemon.</summary>
    public sealed record Availability
    {
        /// <summary>True when avahi-daemon is reachable.</summary>
        public required bool IsAvailable { get; init; }

        /// <summary>Why not, phrased for the operator. Null when available.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>Checks that the tools exist and the daemon answers.</summary>
    public async Task<Availability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // avahi-browse against the daemon's own socket: succeeds only if it is actually up,
            // where checking for the binary would only prove the package is installed.
            var result = await systemRunner
                .RunAsync("avahi-browse", ["--version"], cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return new Availability { IsAvailable = false, Reason = result.StandardError.Trim() };
            }

            var daemon = await systemRunner
                .RunAsync("systemctl", ["is-active", "avahi-daemon"], cancellationToken)
                .ConfigureAwait(false);

            if (daemon.StandardOutput.Trim() != "active")
            {
                return new Availability
                {
                    IsAvailable = false,
                    Reason = "avahi-daemon is not running. Start it with: systemctl start avahi-daemon",
                };
            }

            return new Availability { IsAvailable = true };
        }
        catch (SystemOperationException)
        {
            return new Availability
            {
                IsAvailable = false,
                Reason = "Avahi is not installed. Install it with: apt install avahi-daemon avahi-utils",
            };
        }
    }

    /// <summary>
    /// Browses the network for advertised services.
    /// </summary>
    /// <remarks>
    /// <c>--terminate</c> matters: without it avahi-browse watches forever and the call would sit
    /// until <see cref="ISystemRunner"/>'s timeout killed it. With it, the command returns once
    /// the initial round of responses is in.
    /// </remarks>
    public async Task<ImmutableArray<DiscoveredService>> BrowseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await systemRunner
                .RunAsync("avahi-browse", ["--all", "--resolve", "--parsable", "--terminate", "--no-db-lookup"], cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                logger.LogWarning("avahi-browse failed: {Error}", result.StandardError.Trim());

                return [];
            }

            return Parse(result.StandardOutput);
        }
        catch (SystemOperationException ex)
        {
            logger.LogDebug(ex, "Could not run avahi-browse.");

            return [];
        }
    }

    /// <summary>
    /// Parses <c>avahi-browse --parsable</c> output.
    /// </summary>
    /// <remarks>
    /// Resolved records start with <c>=</c> and are semicolon-separated:
    /// <code>
    /// =;eth0;IPv4;name;_http._tcp;local;host.local;192.168.1.5;80;"txt" "txt"
    /// 0 1     2    3    4         5     6          7           8  9
    /// </code>
    /// Only <c>=</c> lines carry an address and port; <c>+</c> lines are unresolved announcements
    /// of the same service and would double every row.
    /// </remarks>
    private static ImmutableArray<DiscoveredService> Parse(string output)
    {
        var services = new List<DiscoveredService>();

        foreach (var line in output.AsSpan().EnumerateLines())
        {
            var text = line.ToString();

            if (!text.StartsWith("=;", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = text.Split(';');

            if (fields.Length < 9)
            {
                continue;
            }

            if (!int.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            {
                continue;
            }

            services.Add(new DiscoveredService
            {
                Interface = fields[1],
                Protocol = fields[2],

                Name = UnescapeName(fields[3]),
                ServiceType = fields[4],
                Domain = fields[5],
                HostName = fields[6],
                Address = fields[7],
                Port = port,
                Txt = fields.Length > 9 ? string.Join(';', fields[9..]).Trim() : string.Empty,
            });
        }

        // The same service is announced once per interface and per IP protocol; a browser sees
        // one service, so this shows one row.
        return
        [
            .. services
                .DistinctBy(s => (s.Name, s.ServiceType, s.Port))
                .OrderBy(s => s.ServiceType, StringComparer.Ordinal)
                .ThenBy(s => s.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Decodes the <c>\ddd</c> decimal escapes avahi writes in parsable output.
    /// </summary>
    /// <remarks>
    /// Not just <c>\032</c> for space: any byte outside the safe set is escaped this way, so an
    /// AirPlay name like <c>86358372BFA8@SoloMacbook</c> arrives as <c>86358372BFA8\064SoloMacbook</c>.
    /// Handling only spaces leaves a mojibake name on screen.
    /// </remarks>
    private static string UnescapeName(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\'
                && i + 3 < value.Length
                && int.TryParse(
                    value.AsSpan(i + 1, 3),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var code)
                && code is >= 0 and <= 255)
            {
                result.Append((char)code);
                i += 3;

                continue;
            }

            result.Append(value[i]);
        }

        return result.ToString();
    }
}

/// <summary>A service seen on the network.</summary>
public sealed record DiscoveredService
{
    /// <summary>Interface it was seen on.</summary>
    public required string Interface { get; init; }

    /// <summary>IPv4 or IPv6.</summary>
    public required string Protocol { get; init; }

    /// <summary>Advertised service name.</summary>
    public required string Name { get; init; }

    /// <summary>DNS-SD type, for example <c>_http._tcp</c>.</summary>
    public required string ServiceType { get; init; }

    /// <summary>mDNS domain, normally <c>local</c>.</summary>
    public required string Domain { get; init; }

    /// <summary>Advertising host, for example <c>debian.local</c>.</summary>
    public required string HostName { get; init; }

    /// <summary>Resolved address.</summary>
    public required string Address { get; init; }

    /// <summary>Advertised port.</summary>
    public required int Port { get; init; }

    /// <summary>Raw TXT records.</summary>
    public required string Txt { get; init; }

    /// <summary>True when this service came from this machine.</summary>
    public bool IsLocal(string hostName) =>
        HostName.StartsWith($"{hostName}.", StringComparison.OrdinalIgnoreCase)
        || HostName.Equals(hostName, StringComparison.OrdinalIgnoreCase);
}
