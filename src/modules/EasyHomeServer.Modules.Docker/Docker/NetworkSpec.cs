using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// What the operator typed into the create-network form.
/// </summary>
/// <remarks>
/// The validation here exists because Docker's does not. <c>docker network create -d macvlan
/// foo</c> succeeds with no parent and no subnet, producing a network whose containers get a
/// private address and cannot reach anything. Docker reports success; the failure surfaces later
/// as "Network unreachable" inside a container. Everything a macvlan actually needs is therefore
/// required before the command is built.
/// </remarks>
public sealed partial class NetworkSpec
{
    /// <summary>Network name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Driver: bridge, macvlan or ipvlan.</summary>
    public string Driver { get; set; } = "bridge";

    /// <summary>Parent interface for a macvlan or ipvlan. Must be a real NIC.</summary>
    public string Parent { get; set; } = string.Empty;

    /// <summary>Subnet in CIDR form. Must be the parent's own subnet.</summary>
    public string Subnet { get; set; } = string.Empty;

    /// <summary>Gateway address, normally the router.</summary>
    public string Gateway { get; set; } = string.Empty;

    /// <summary>
    /// Slice of the subnet Docker may allocate from. Optional, but leaving it empty invites an
    /// address collision with the router's DHCP pool.
    /// </summary>
    public string IpRange { get; set; } = string.Empty;

    /// <summary>
    /// Whether to give this host a shim interface so it can reach containers on this network.
    /// </summary>
    /// <remarks>
    /// Off by default: it is host network configuration, and a macvlan is often chosen precisely
    /// to keep the containers separate from the host. Requires an IP range, since the shim takes
    /// its address from inside it. See <see cref="MacvlanShim"/>.
    /// </remarks>
    public bool CreateHostShim { get; set; }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NetworkNamePattern { get; }

    /// <summary>True for drivers that place containers directly on the physical network.</summary>
    public bool IsLanAddressed => Driver is "macvlan" or "ipvlan";

    /// <summary>Returns the first validation error, or null when the spec is usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !NetworkNamePattern.IsMatch(Name))
        {
            return "Start with a letter or digit; letters, digits, dots, dashes and underscores only.";
        }

        if (Driver is not ("bridge" or "macvlan" or "ipvlan"))
        {
            return $"Driver '{Driver}' is not supported here.";
        }

        if (!IsLanAddressed)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(Parent))
        {
            return "Choose the parent interface the containers will share.";
        }

        if (ValidateCidr(Subnet) is { } subnetError)
        {
            return $"Subnet: {subnetError}";
        }

        if (!IPAddress.TryParse(Gateway, out var gateway)
            || gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return "Gateway must be an IPv4 address, normally your router.";
        }

        if (!IsInSubnet(gateway, Subnet))
        {
            return $"Gateway {Gateway} is not inside {Subnet}.";
        }

        if (CreateHostShim && IpRange.Length == 0)
        {
            // The shim's address comes from inside the range so the whole arrangement stays
            // within one block reserved from DHCP; without a range there is nowhere safe to put it.
            return "Letting this server reach the containers needs an IP range: the shim takes the "
                   + "first address in it.";
        }

        if (IpRange is { Length: > 0 })
        {
            if (ValidateCidr(IpRange) is { } rangeError)
            {
                return $"IP range: {rangeError}";
            }

            // A range outside the subnet is accepted by docker and then allocates addresses that
            // route nowhere.
            if (!IsInSubnet(IPAddress.Parse(IpRange.Split('/')[0]), Subnet))
            {
                return $"IP range {IpRange} is not inside {Subnet}.";
            }

            var subnetPrefix = int.Parse(Subnet.Split('/')[1], CultureInfo.InvariantCulture);
            var rangePrefix = int.Parse(IpRange.Split('/')[1], CultureInfo.InvariantCulture);

            if (rangePrefix < subnetPrefix)
            {
                return $"IP range {IpRange} is larger than the subnet. Use a smaller slice, such as /28.";
            }
        }

        return null;
    }

    private static string? ValidateCidr(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "required, in CIDR form such as 192.168.1.0/24.";
        }

        var parts = value.Split('/');

        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix)
            || prefix is < 8 or > 30)
        {
            return "use CIDR form such as 192.168.1.0/24, with a prefix between 8 and 30.";
        }

        return null;
    }

    private static bool IsInSubnet(IPAddress address, string subnet)
    {
        var parts = subnet.Split('/');

        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var network)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        var addressBits = ToUInt32(address);
        var networkBits = ToUInt32(network);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

        return (addressBits & mask) == (networkBits & mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
