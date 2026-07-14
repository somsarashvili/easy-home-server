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
public sealed class DockerCli(ISystemRunner systemRunner, ILogger<DockerCli> logger)
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

    /// <summary>Removes a network.</summary>
    public Task<DockerActionResult> RemoveNetworkAsync(string id, CancellationToken cancellationToken = default) =>
        ActionAsync(["network", "rm", id], $"remove network {id}", cancellationToken);

    /// <summary>Removes every dangling image.</summary>
    public Task<DockerActionResult> PruneImagesAsync(CancellationToken cancellationToken = default) =>
        ActionAsync(["image", "prune", "--force"], "prune images", cancellationToken);

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
