using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// Gives the host a way to reach containers on a macvlan network, via a systemd unit that
/// survives reboots.
/// </summary>
/// <remarks>
/// <para>
/// The kernel refuses to pass traffic between a macvlan parent and its own children, so a
/// server cannot reach the containers running on it — <c>ping</c> to a container on its own
/// macvlan simply times out. That is by design, and it surprises everyone.
/// </para>
/// <para>
/// The fix is a second macvlan interface on the same parent: two macvlan <em>siblings</em> can
/// talk, even though parent and child cannot. So the host gets its own sibling, an address, and
/// a route pointing the container range at it. Traffic then leaves the host and comes back the
/// normal way instead of being short-circuited.
/// </para>
/// <para>
/// It lives in a systemd unit rather than as bare <c>ip</c> commands because those leave nothing
/// behind: the shim would vanish on the next reboot and the containers would go unreachable
/// again with no clue why. A unit is also the one mechanism that works whether the machine runs
/// ifupdown, systemd-networkd or NetworkManager, none of which this module should have to know
/// about.
/// </para>
/// <para>
/// It only ever adds an interface, an address and one route. It never touches the parent, so the
/// worst case is that the shim fails to appear and the host still cannot reach the containers —
/// exactly where it started. It cannot take the machine's networking down.
/// </para>
/// </remarks>
public sealed partial class MacvlanShim(ISystemRunner systemRunner, ILogger<MacvlanShim> logger)
{
    /// <summary>
    /// Runtime-generated units belong in /etc/systemd/system; /lib/systemd/system is for files
    /// owned by a package.
    /// </summary>
    private const string UnitDirectory = "/etc/systemd/system";

    private const string UnitPrefix = "easyhomeserver-macvlan-shim-";

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex UnsafeNameChars { get; }

    /// <summary>Unit name for a network's shim.</summary>
    public static string UnitName(string networkName) => $"{UnitPrefix}{Sanitise(networkName)}.service";

    /// <summary>Full path of the unit file.</summary>
    public static string UnitPath(string networkName) => Path.Combine(UnitDirectory, UnitName(networkName));

    /// <summary>
    /// Interface name for a network's shim.
    /// </summary>
    /// <remarks>
    /// The kernel caps an interface name at 15 characters (IFNAMSIZ), and `ip link add` fails
    /// outright above that, so the network name is truncated to fit rather than trusted.
    /// </remarks>
    public static string InterfaceName(string networkName)
    {
        const int maxLength = 15;
        var name = $"ehs-{Sanitise(networkName)}";

        return name.Length <= maxLength ? name : name[..maxLength].TrimEnd('-');
    }

    private static string Sanitise(string name) =>
        UnsafeNameChars.Replace(name.ToLowerInvariant(), "-").Trim('-') is { Length: > 0 } cleaned
            ? cleaned
            : "unnamed";

    /// <summary>Whether a shim unit exists for this network.</summary>
    public bool IsInstalled(string networkName) => File.Exists(UnitPath(networkName));

    /// <summary>
    /// The sanitised network names that currently have a shim unit.
    /// </summary>
    /// <remarks>
    /// Read from disk rather than tracked in memory, so it stays correct across a restart of this
    /// process — the units outlive it.
    /// </remarks>
    public ImmutableArray<string> ListInstalled()
    {
        try
        {
            if (!Directory.Exists(UnitDirectory))
            {
                return [];
            }

            return
            [
                .. Directory
                    .GetFiles(UnitDirectory, $"{UnitPrefix}*.service", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Select(f => f![UnitPrefix.Length..^".service".Length])
                    .OrderBy(n => n, StringComparer.Ordinal),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list macvlan shim units.");

            return [];
        }
    }

    /// <summary>
    /// Removes shims whose network no longer exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removing a network through this module takes its shim with it, but a network removed with
    /// <c>docker network rm</c> — or by <c>compose down</c>, or by anything else — does not. The
    /// orphan is not harmless: its unit recreates an interface on every boot and installs a route
    /// for a range nothing owns any more. So the truth is reconciled against what Docker actually
    /// has, rather than trusted to arrive as an event.
    /// </para>
    /// <para>
    /// <paramref name="existingNetworks"/> must be a <em>complete</em> list. Docker always has at
    /// least bridge, host and none, so an empty one means the query failed, not that every network
    /// is gone — and acting on it would tear down every shim on the machine over a transient
    /// error.
    /// </para>
    /// </remarks>
    public async Task ReconcileAsync(
        IReadOnlyCollection<string> existingNetworks,
        CancellationToken cancellationToken = default)
    {
        if (existingNetworks.Count == 0)
        {
            return;
        }

        // Compared on the sanitised name, since that is what the unit is named after and two
        // network names could sanitise to the same thing.
        var alive = existingNetworks.Select(Sanitise).ToHashSet(StringComparer.Ordinal);

        foreach (var installed in ListInstalled())
        {
            if (alive.Contains(installed))
            {
                continue;
            }

            logger.LogInformation(
                "Removing the macvlan shim for {Network}: that network no longer exists.",
                installed);

            await RemoveAsync(installed, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The address the host takes on the shim: the first address of the container range.
    /// </summary>
    /// <remarks>
    /// Taken from inside the range, not next to it, so the whole arrangement stays within the
    /// one block reserved from DHCP. Picking an address just outside would sit in the router's
    /// pool and eventually be leased to something else. Docker is told to keep it free with
    /// <c>--aux-address</c>.
    /// </remarks>
    public static string? ShimAddressFor(string ipRange)
    {
        var parts = ipRange.Split('/');

        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address))
        {
            return null;
        }

        return address.ToString();
    }

    /// <summary>Installs and starts the shim for a network.</summary>
    public async Task<DockerActionResult> InstallAsync(
        string networkName,
        string parent,
        string shimAddress,
        string ipRange,
        CancellationToken cancellationToken = default)
    {
        var unit = UnitName(networkName);
        var path = UnitPath(networkName);
        var interfaceName = InterfaceName(networkName);

        try
        {
            await systemRunner
                .WriteFileAsync(path, BuildUnit(networkName, parent, interfaceName, shimAddress, ipRange), cancellationToken)
                .ConfigureAwait(false);

            // Until systemd rereads its units the new one does not exist, and enable fails with
            // a confusing "not found".
            await systemRunner.SystemctlAsync(SystemctlAction.DaemonReload, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await systemRunner.SystemctlAsync(SystemctlAction.Enable, unit, cancellationToken).ConfigureAwait(false);

            var start = await systemRunner
                .SystemctlAsync(SystemctlAction.Start, unit, cancellationToken)
                .ConfigureAwait(false);

            if (!start.Succeeded)
            {
                return new DockerActionResult
                {
                    Succeeded = false,
                    Message = $"The network was created, but its host shim failed to start: {start.StandardError.Trim()}",
                };
            }

            logger.LogInformation(
                "Installed macvlan shim {Interface} ({Address}) for network {Network} on {Parent}.",
                interfaceName,
                shimAddress,
                networkName,
                parent);

            return new DockerActionResult
            {
                Succeeded = true,
                Message = $"This server can now reach containers on '{networkName}' via {interfaceName}.",
            };
        }
        catch (SystemOperationException ex)
        {
            logger.LogError(ex, "Could not install the macvlan shim for {Network}.", networkName);

            return new DockerActionResult
            {
                Succeeded = false,
                Message = $"The network was created, but its host shim could not be installed: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Stops and removes a network's shim. Safe to call when none exists.
    /// </summary>
    /// <remarks>
    /// Called when the network is removed: a shim left behind would recreate an interface at
    /// every boot, routing a range that no longer exists.
    /// </remarks>
    public async Task RemoveAsync(string networkName, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled(networkName))
        {
            return;
        }

        var unit = UnitName(networkName);

        try
        {
            // Stop first: stopping deletes the interface, and deleting the unit before that
            // would strip away the ExecStop that does it.
            await systemRunner.SystemctlAsync(SystemctlAction.Stop, unit, cancellationToken).ConfigureAwait(false);
            await systemRunner.SystemctlAsync(SystemctlAction.Disable, unit, cancellationToken).ConfigureAwait(false);

            File.Delete(UnitPath(networkName));

            await systemRunner.SystemctlAsync(SystemctlAction.DaemonReload, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Removed the macvlan shim for network {Network}.", networkName);
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not fully remove the macvlan shim for {Network}.", networkName);
        }
    }

    /// <summary>
    /// Renders the unit.
    /// </summary>
    /// <remarks>
    /// <c>Type=oneshot</c> with <c>RemainAfterExit</c>: this configures and exits rather than
    /// staying resident, but systemd must still consider it active so that stopping it runs the
    /// teardown.
    /// </remarks>
    private static string BuildUnit(
        string networkName,
        string parent,
        string interfaceName,
        string shimAddress,
        string ipRange)
    {
        return $"""
                # Generated by EasyHomeServer for the Docker macvlan network "{networkName}".
                # Removed automatically when that network is removed.
                #
                # The kernel does not let a macvlan parent talk to its own children, so this host
                # cannot reach containers on {networkName}. This adds a macvlan sibling for the host
                # and routes the container range to it; siblings can talk to each other.

                [Unit]
                Description=macvlan shim so this host can reach containers on the "{networkName}" Docker network
                After=network-online.target
                Wants=network-online.target

                [Service]
                Type=oneshot
                RemainAfterExit=yes

                # Leading "-": a shim left over from an unclean stop must not fail the restart.
                ExecStartPre=-/usr/sbin/ip link del {interfaceName}

                ExecStart=/usr/sbin/ip link add {interfaceName} link {parent} type macvlan mode bridge
                ExecStart=/usr/sbin/ip addr add {shimAddress}/32 dev {interfaceName}
                ExecStart=/usr/sbin/ip link set {interfaceName} up
                ExecStart=/usr/sbin/ip route add {ipRange} dev {interfaceName}

                # Deleting the link takes its addresses and routes with it.
                ExecStop=-/usr/sbin/ip link del {interfaceName}

                [Install]
                WantedBy=multi-user.target

                """;
    }
}
