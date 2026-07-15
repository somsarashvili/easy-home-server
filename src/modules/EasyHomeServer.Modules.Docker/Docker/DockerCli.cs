using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// Talks to the Docker daemon by driving the <c>docker</c> CLI through <see cref="ISystemRunner"/>.
/// </summary>
/// <remarks>
/// <para>
/// Not the Engine API over <c>/var/run/docker.sock</c>, deliberately. The socket is a
/// privileged resource — membership of the <c>docker</c> group is famously equivalent to root —
/// and reaching it directly would be the one piece of module code that bypasses
/// <see cref="ISystemRunner"/>. Going through the seam means this module needs no changes when
/// privileged work moves to a separate worker process, and it keeps the module's dependency
/// list empty. The cost is polling rather than a streaming event feed, which a home server can
/// afford.
/// </para>
/// <para>
/// Reads use <c>docker inspect</c> rather than <c>docker ps --format json</c>. The latter emits
/// *display* JSON: the command is truncated with an ellipsis, ports and timestamps are
/// preformatted strings, and ids are shortened. <c>inspect</c> returns the same schema the API
/// serves, in full.
/// </para>
/// </remarks>
public sealed class DockerCli(
    ISystemRunner systemRunner,
    DockerOptions options,
    MacvlanShim shim,
    ILogger<DockerCli> logger)
{
    private const string Executable = "docker";

    /// <summary>Result of probing for a usable daemon.</summary>
    public sealed record Availability
    {
        /// <summary>True when the CLI ran and the daemon answered.</summary>
        public required bool IsAvailable { get; init; }

        /// <summary>Why it is unavailable, phrased for the operator. Null when available.</summary>
        public string? Reason { get; init; }

        /// <summary>Server version string when available.</summary>
        public string? Version { get; init; }
    }

    /// <summary>
    /// Checks that the CLI exists and the daemon is reachable. Distinguishes "not installed"
    /// from "installed but not running" — the fix differs, so the page should not conflate them.
    /// </summary>
    public async Task<Availability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await systemRunner
                .RunAsync(Executable, ["version", "--format", "{{.Server.Version}}"], cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                return new Availability { IsAvailable = true, Version = result.StandardOutput.Trim() };
            }

            // The CLI exits non-zero when it cannot reach the daemon, and says so on stderr.
            var detail = result.StandardError.Trim();

            return new Availability
            {
                IsAvailable = false,
                Reason = detail.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
                    ? "The Docker daemon is not running. Start it with: systemctl start docker"
                    : detail.Length > 0 ? detail : "docker exited with an error.",
            };
        }
        catch (SystemOperationException)
        {
            // RunAsync throws only when the executable itself cannot be launched, which on a
            // machine with a working PATH means docker is not installed.
            return new Availability
            {
                IsAvailable = false,
                Reason = "Docker is not installed on this machine. Install it with: apt install docker.io",
            };
        }
    }

    /// <summary>Lists every container, running or not, with full detail.</summary>
    public async Task<ImmutableArray<DockerContainer>> ListContainersAsync(
        CancellationToken cancellationToken = default)
    {
        var ids = await ListIdsAsync(["ps", "-aq", "--no-trunc"], cancellationToken).ConfigureAwait(false);

        if (ids.Length == 0)
        {
            return [];
        }

        var json = await InspectAsync("container", ids, cancellationToken).ConfigureAwait(false);

        return Parse(json, DockerJson.ParseContainer, "container");
    }

    /// <summary>Lists every image, including dangling ones.</summary>
    public async Task<ImmutableArray<DockerImage>> ListImagesAsync(CancellationToken cancellationToken = default)
    {
        var ids = await ListIdsAsync(["image", "ls", "-aq", "--no-trunc"], cancellationToken).ConfigureAwait(false);

        if (ids.Length == 0)
        {
            return [];
        }

        // An image id appears once per tag, and inspecting the same id twice returns it twice.
        var json = await InspectAsync("image", [.. ids.Distinct()], cancellationToken).ConfigureAwait(false);

        return Parse(json, DockerJson.ParseImage, "image");
    }

    /// <summary>Lists every volume.</summary>
    public async Task<ImmutableArray<DockerVolume>> ListVolumesAsync(CancellationToken cancellationToken = default)
    {
        var names = await ListIdsAsync(["volume", "ls", "-q"], cancellationToken).ConfigureAwait(false);

        if (names.Length == 0)
        {
            return [];
        }

        var result = await RunAsync(["volume", "inspect", .. names], cancellationToken).ConfigureAwait(false);

        return result is null ? [] : Parse(result, DockerJson.ParseVolume, "volume");
    }

    /// <summary>Lists every network.</summary>
    public async Task<ImmutableArray<DockerNetwork>> ListNetworksAsync(CancellationToken cancellationToken = default)
    {
        var ids = await ListIdsAsync(["network", "ls", "-q", "--no-trunc"], cancellationToken).ConfigureAwait(false);

        if (ids.Length == 0)
        {
            return [];
        }

        var result = await RunAsync(["network", "inspect", .. ids], cancellationToken).ConfigureAwait(false);

        return result is null ? [] : Parse(result, DockerJson.ParseNetwork, "network");
    }

    /// <summary>
    /// Fetches the tail of a container's logs. Bounded on purpose: a container that has been up
    /// for months can hold gigabytes, and an unbounded read would pull all of it into memory and
    /// then into a browser.
    /// </summary>
    public async Task<string> GetLogsAsync(
        string containerId,
        int tailLines = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var result = await systemRunner
            .RunAsync(
                Executable,
                ["logs", "--tail", tailLines.ToString(CultureInfo.InvariantCulture), "--timestamps", containerId],
                cancellationToken)
            .ConfigureAwait(false);

        // Docker writes container stderr to our stderr, so both streams are real log output.
        var combined = string.Concat(result.StandardOutput, result.StandardError);

        return combined.Length == 0 ? "(no output)" : combined;
    }

    /// <summary>Starts a container.</summary>
    public Task<DockerActionResult> StartAsync(string id, CancellationToken cancellationToken = default) =>
        ActionAsync(["start", id], $"start {id}", cancellationToken);

    /// <summary>
    /// Stops a container, giving it <paramref name="timeoutSeconds"/> to exit before SIGKILL.
    /// </summary>
    public Task<DockerActionResult> StopAsync(
        string id,
        int timeoutSeconds = 10,
        CancellationToken cancellationToken = default) =>
        ActionAsync(
            ["stop", "--time", timeoutSeconds.ToString(CultureInfo.InvariantCulture), id],
            $"stop {id}",
            cancellationToken);

    /// <summary>Restarts a container.</summary>
    public Task<DockerActionResult> RestartAsync(
        string id,
        int timeoutSeconds = 10,
        CancellationToken cancellationToken = default) =>
        ActionAsync(
            ["restart", "--time", timeoutSeconds.ToString(CultureInfo.InvariantCulture), id],
            $"restart {id}",
            cancellationToken);

    /// <summary>Pauses a container's processes.</summary>
    public Task<DockerActionResult> PauseAsync(string id, CancellationToken cancellationToken = default) =>
        ActionAsync(["pause", id], $"pause {id}", cancellationToken);

    /// <summary>Resumes a paused container.</summary>
    public Task<DockerActionResult> UnpauseAsync(string id, CancellationToken cancellationToken = default) =>
        ActionAsync(["unpause", id], $"unpause {id}", cancellationToken);

    /// <summary>Removes a container. <paramref name="force"/> kills it first if running.</summary>
    public Task<DockerActionResult> RemoveContainerAsync(
        string id,
        bool force = false,
        CancellationToken cancellationToken = default) =>
        ActionAsync(
            force ? ["rm", "--force", id] : ["rm", id],
            $"remove container {id}",
            cancellationToken);

    /// <summary>Removes an image.</summary>
    public Task<DockerActionResult> RemoveImageAsync(
        string id,
        bool force = false,
        CancellationToken cancellationToken = default) =>
        ActionAsync(
            force ? ["image", "rm", "--force", id] : ["image", "rm", id],
            $"remove image {id}",
            cancellationToken);

    /// <summary>Removes a volume, destroying its data.</summary>
    public Task<DockerActionResult> RemoveVolumeAsync(string name, CancellationToken cancellationToken = default) =>
        ActionAsync(["volume", "rm", name], $"remove volume {name}", cancellationToken);

    /// <summary>
    /// Removes a network, and its host shim if it has one.
    /// </summary>
    /// <remarks>
    /// The shim goes with it. Left behind, its unit would recreate an interface on every boot and
    /// route a range belonging to a network that no longer exists.
    /// </remarks>
    public async Task<DockerActionResult> RemoveNetworkAsync(
        string id,
        string name,
        CancellationToken cancellationToken = default)
    {
        var result = await ActionAsync(["network", "rm", id], $"remove network {name}", cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            await shim.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Removes every dangling image.</summary>
    public Task<DockerActionResult> PruneImagesAsync(CancellationToken cancellationToken = default) =>
        ActionAsync(["image", "prune", "--force"], "prune images", cancellationToken);

    /// <summary>Creates a volume.</summary>
    public Task<DockerActionResult> CreateVolumeAsync(
        string name,
        string driver = "local",
        CancellationToken cancellationToken = default) =>
        ActionAsync(["volume", "create", "--driver", driver, name], $"create volume {name}", cancellationToken);

    /// <summary>
    /// Creates a network.
    /// </summary>
    /// <remarks>
    /// A macvlan needs a parent interface, a subnet and a gateway. Docker does not enforce that:
    /// <c>docker network create -d macvlan foo</c> succeeds and quietly produces a network with a
    /// private subnet and no parent, whose containers get an address and then find the network
    /// unreachable. It looks like it worked, which is worse than failing, so <see cref="NetworkSpec"/>
    /// requires them up front.
    /// </remarks>
    public async Task<DockerActionResult> CreateNetworkAsync(
        NetworkSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Validate() is { } error)
        {
            return new DockerActionResult { Succeeded = false, Message = error };
        }

        var arguments = new List<string> { "network", "create", "--driver", spec.Driver };
        string? shimAddress = null;

        if (spec.IsLanAddressed)
        {
            arguments.Add("--subnet");
            arguments.Add(spec.Subnet);
            arguments.Add("--gateway");
            arguments.Add(spec.Gateway);

            // Without an ip-range docker allocates from the whole subnet and will hand a
            // container an address the router later leases to a phone.
            if (spec.IpRange is { Length: > 0 })
            {
                arguments.Add("--ip-range");
                arguments.Add(spec.IpRange);
            }

            if (spec.CreateHostShim && MacvlanShim.ShimAddressFor(spec.IpRange) is { } address)
            {
                shimAddress = address;

                // Reserves the shim's address so docker never hands it to a container. Without
                // this the first container would take it and collide with the host, which
                // presents as intermittent, baffling connectivity.
                arguments.Add("--aux-address");
                arguments.Add($"host={address}");
            }

            arguments.Add("--opt");
            arguments.Add($"parent={spec.Parent}");
        }

        arguments.Add(spec.Name);

        var result = await ActionAsync([.. arguments], $"create network {spec.Name}", cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || shimAddress is null)
        {
            return result;
        }

        // The network exists at this point. If the shim fails, say so plainly rather than
        // rolling the network back — the network is useful without it, and silently undoing
        // what the operator asked for is worse than a partial success they can act on.
        return await shim
            .InstallAsync(spec.Name, spec.Parent, shimAddress, spec.IpRange, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the host's physical network interfaces, as candidate macvlan parents.
    /// </summary>
    /// <remarks>
    /// Read from <c>/sys/class/net</c>: an interface with a <c>device</c> symlink is backed by
    /// real hardware, which is exactly what a macvlan parent must be. Everything docker itself
    /// created — <c>docker0</c>, the <c>veth</c> pairs, <c>lo</c> — has no such link and is
    /// filtered out, so the list only offers interfaces that can actually work.
    /// </remarks>
    public ImmutableArray<string> ListPhysicalInterfaces()
    {
        try
        {
            if (!Directory.Exists("/sys/class/net"))
            {
                return [];
            }

            return
            [
                .. Directory
                    .GetDirectories("/sys/class/net")
                    .Where(d => Directory.Exists(Path.Combine(d, "device")))
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.Ordinal),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list physical interfaces.");

            return [];
        }
    }

    /// <summary>
    /// The address and prefix on an interface, used to prefill a macvlan's subnet.
    /// </summary>
    /// <remarks>
    /// A macvlan's subnet must be the parent's real subnet — containers share that broadcast
    /// domain. Asking the operator to type it is asking them to get it wrong.
    /// </remarks>
    public async Task<(string? Subnet, string? Gateway)> DescribeInterfaceAsync(
        string interfaceName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var address = await systemRunner
                .RunAsync("ip", ["-4", "-oneline", "addr", "show", "dev", interfaceName], cancellationToken)
                .ConfigureAwait(false);

            string? subnet = null;

            // "2: enp0s1    inet 192.168.64.8/24 brd ..." -> the network 192.168.64.0/24
            var inet = address.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.IndexOf(inet, "inet");

            if (index >= 0 && index + 1 < inet.Length)
            {
                subnet = ToNetworkAddress(inet[index + 1]);
            }

            var route = await systemRunner
                .RunAsync("ip", ["-4", "route", "show", "default", "dev", interfaceName], cancellationToken)
                .ConfigureAwait(false);

            // "default via 192.168.64.1 proto dhcp ..."
            var fields = route.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var via = Array.IndexOf(fields, "via");
            var gateway = via >= 0 && via + 1 < fields.Length ? fields[via + 1] : null;

            return (subnet, gateway);
        }
        catch (SystemOperationException ex)
        {
            logger.LogWarning(ex, "Could not describe interface {Interface}.", interfaceName);

            return (null, null);
        }
    }

    /// <summary>Turns an address with a prefix (192.168.64.8/24) into its network (192.168.64.0/24).</summary>
    private static string? ToNetworkAddress(string addressWithPrefix)
    {
        var parts = addressWithPrefix.Split('/');

        if (parts.Length != 2
            || !System.Net.IPAddress.TryParse(parts[0], out var address)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || prefix is < 0 or > 32)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

        for (var i = 0; i < 4; i++)
        {
            bytes[i] &= (byte)((mask >> ((3 - i) * 8)) & 0xFF);
        }

        return $"{new System.Net.IPAddress(bytes)}/{prefix}";
    }

    /// <summary>
    /// Creates and starts a container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value the operator typed is passed as its own argument via
    /// <see cref="ISystemRunner"/>, which never builds a shell command line. A port mapping of
    /// <c>8080:80; rm -rf /</c> is therefore handed to docker as one nonsensical argument and
    /// rejected, rather than being run.
    /// </para>
    /// <para>
    /// The long timeout is because this pulls the image when it is not present locally.
    /// </para>
    /// </remarks>
    public async Task<DockerActionResult> CreateContainerAsync(
        ContainerSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Validate() is { } error)
        {
            return new DockerActionResult { Succeeded = false, Message = error };
        }

        var arguments = new List<string> { "run", "--detach", "--name", spec.Name };

        if (spec.RestartPolicy is { Length: > 0 } restart && restart != "no")
        {
            arguments.Add("--restart");
            arguments.Add(restart);
        }

        foreach (var port in spec.ParsePorts())
        {
            arguments.Add("--publish");
            arguments.Add(port);
        }

        foreach (var volume in spec.ParseVolumes())
        {
            arguments.Add("--volume");
            arguments.Add(volume);
        }

        foreach (var variable in spec.ParseEnvironment())
        {
            arguments.Add("--env");
            arguments.Add(variable);
        }

        foreach (var label in spec.ParseLabels())
        {
            arguments.Add("--label");
            arguments.Add(label);
        }

        if (spec.Network is { Length: > 0 } network)
        {
            arguments.Add("--network");
            arguments.Add(network);
        }

        arguments.Add(spec.Image);

        // Anything after the image is the container's own command, not docker's.
        arguments.AddRange(spec.ParseCommand());

        logger.LogInformation("docker {Arguments}", string.Join(' ', arguments));

        try
        {
            var result = await systemRunner
                .RunAsync(Executable, arguments, options.ComposeUpTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                return new DockerActionResult { Succeeded = true, Message = $"Created {spec.Name}." };
            }

            var detail = result.StandardError.Trim();

            return new DockerActionResult
            {
                Succeeded = false,
                Message = detail.Length > 0 ? detail : $"Could not create {spec.Name}: exit code {result.ExitCode}.",
            };
        }
        catch (SystemOperationException ex)
        {
            return new DockerActionResult { Succeeded = false, Message = $"Could not create {spec.Name}: {ex.Message}" };
        }
    }

    private async Task<DockerActionResult> ActionAsync(
        string[] arguments,
        string description,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("docker {Arguments}", string.Join(' ', arguments));

        try
        {
            var result = await systemRunner.RunAsync(Executable, arguments, cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                // Callers that know the container's name phrase their own success message; this
                // is the fallback and is not meant to be read by a person.
                return new DockerActionResult { Succeeded = true, Message = $"Did {description}." };
            }

            // Docker's stderr is written for humans and is more useful than anything invented here.
            var detail = result.StandardError.Trim();

            logger.LogWarning("Could not {Description}: {Detail}", description, detail);

            return new DockerActionResult
            {
                Succeeded = false,
                Message = detail.Length > 0 ? detail : $"Could not {description}: exit code {result.ExitCode}.",
            };
        }
        catch (SystemOperationException ex)
        {
            logger.LogError(ex, "Could not {Description}.", description);

            return new DockerActionResult { Succeeded = false, Message = $"Could not {description}: {ex.Message}" };
        }
    }

    private async Task<string[]> ListIdsAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var output = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);

        if (output is null)
        {
            return [];
        }

        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private async Task<string> InspectAsync(string type, string[] ids, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["inspect", "--type", type, .. ids], cancellationToken).ConfigureAwait(false);

        return result ?? "[]";
    }

    private async Task<string?> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await systemRunner.RunAsync(Executable, arguments, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "docker {Arguments} exited with {ExitCode}: {Error}",
                    string.Join(' ', arguments),
                    result.ExitCode,
                    result.StandardError.Trim());

                return null;
            }

            return result.StandardOutput;
        }
        catch (SystemOperationException ex)
        {
            logger.LogDebug(ex, "docker {Arguments} could not be run.", string.Join(' ', arguments));

            return null;
        }
    }

    /// <summary>
    /// Parses an inspect array, skipping entries that do not parse rather than losing the batch.
    /// Docker's schema varies across versions and a single odd container should not blank the page.
    /// </summary>
    private ImmutableArray<T> Parse<T>(string json, Func<JsonElement, T?> parse, string what)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var builder = ImmutableArray.CreateBuilder<T>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    if (parse(element) is { } parsed)
                    {
                        builder.Add(parsed);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    logger.LogWarning(ex, "Skipped a {What} whose inspect output could not be parsed.", what);
                }
            }

            return builder.ToImmutable();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "docker inspect returned output that is not valid JSON for {What}.", what);

            return [];
        }
    }
}

/// <summary>Outcome of a Docker action, phrased for display.</summary>
public sealed record DockerActionResult
{
    /// <summary>Whether the command succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Message to show the operator — Docker's own stderr on failure.</summary>
    public required string Message { get; init; }
}
