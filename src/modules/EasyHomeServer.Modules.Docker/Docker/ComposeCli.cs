using System.Collections.Immutable;
using System.Text.RegularExpressions;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// Drives <c>docker compose</c> through <see cref="ISystemRunner"/>, and owns the compose files
/// in the stacks directory.
/// </summary>
public sealed partial class ComposeCli(ISystemRunner systemRunner, DockerOptions options, ILogger<ComposeCli> logger)
{
    private const string Executable = "docker";

    /// <summary>Filename written for a project created here. compose.yaml is the current spec's preferred name.</summary>
    private const string ComposeFileName = "compose.yaml";

    /// <summary>
    /// Project names must be a DNS label: compose derives container, network and volume names
    /// from them. It also keeps the name safe to use as a directory name, which matters because
    /// it is joined onto a path.
    /// </summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex ProjectNamePattern { get; }

    /// <summary>Whether the compose plugin is present.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await systemRunner
                .RunAsync(Executable, ["compose", "version", "--short"], cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded;
        }
        catch (SystemOperationException)
        {
            return false;
        }
    }

    /// <summary>Directory a managed project lives in.</summary>
    public string DirectoryFor(string projectName) => Path.Combine(options.ComposeProjectsPath, projectName);

    /// <summary>Compose file path for a managed project.</summary>
    public string ComposeFileFor(string projectName) => Path.Combine(DirectoryFor(projectName), ComposeFileName);

    /// <summary>
    /// Validates a project name. Rejecting a bad name matters twice over: compose builds resource
    /// names from it, and it is joined onto a filesystem path.
    /// </summary>
    public static string? ValidateProjectName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a project name.";
        }

        if (!ProjectNamePattern.IsMatch(name))
        {
            return "Use lowercase letters, digits, dashes and underscores, starting with a letter or digit.";
        }

        return name.Length > 63 ? "Keep the name under 64 characters." : null;
    }

    /// <summary>
    /// Finds compose projects on disk in the stacks directory, including ones that are fully down.
    /// </summary>
    public ImmutableArray<(string Name, string ConfigFile)> ScanProjectsDirectory()
    {
        try
        {
            if (!Directory.Exists(options.ComposeProjectsPath))
            {
                return [];
            }

            var found = ImmutableArray.CreateBuilder<(string, string)>();

            foreach (var directory in Directory.GetDirectories(options.ComposeProjectsPath))
            {
                // Both spellings, current and legacy, in the order compose itself prefers them.
                var candidate = new[] { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" }
                    .Select(f => Path.Combine(directory, f))
                    .FirstOrDefault(File.Exists);

                if (candidate is not null)
                {
                    found.Add((Path.GetFileName(directory), candidate));
                }
            }

            return found.ToImmutable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not scan {ProjectsPath}.", options.ComposeProjectsPath);

            return [];
        }
    }

    /// <summary>Reads a project's compose file.</summary>
    public async Task<string?> ReadConfigFileAsync(ComposeProject project, CancellationToken cancellationToken = default)
    {
        var path = project.ConfigFiles.FirstOrDefault();

        if (path is null)
        {
            return null;
        }

        try
        {
            return await systemRunner.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SystemOperationException ex)
        {
            logger.LogWarning(ex, "Could not read {Path}.", path);

            return null;
        }
    }

    /// <summary>
    /// Checks that a compose file parses, without applying it.
    /// </summary>
    /// <remarks>
    /// Written to a temporary file and handed to <c>compose config</c> rather than parsed here.
    /// Compose is the only thing that agrees with compose about what is valid — a YAML parser
    /// would accept files it rejects, and this way the operator sees compose's own error message.
    /// Validating before saving means a typo cannot replace a working project's file.
    /// </remarks>
    public async Task<DockerActionResult> ValidateAsync(string yaml, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new DockerActionResult { Succeeded = false, Message = "The compose file is empty." };
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ehs-compose-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var path = Path.Combine(temporaryDirectory, ComposeFileName);
            await File.WriteAllTextAsync(path, yaml, cancellationToken).ConfigureAwait(false);

            var result = await systemRunner
                .RunAsync(Executable, ["compose", "-f", path, "config", "--quiet"], cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                return new DockerActionResult { Succeeded = true, Message = "The compose file is valid." };
            }

            return new DockerActionResult
            {
                Succeeded = false,
                Message = result.StandardError.Trim() is { Length: > 0 } error ? error : "The compose file is not valid.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemOperationException)
        {
            return new DockerActionResult { Succeeded = false, Message = $"Could not validate: {ex.Message}" };
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not clean up {TemporaryDirectory}.", temporaryDirectory);
            }
        }
    }

    /// <summary>Writes a managed project's compose file, creating its directory.</summary>
    public async Task<DockerActionResult> SaveAsync(
        string projectName,
        string yaml,
        CancellationToken cancellationToken = default)
    {
        if (ValidateProjectName(projectName) is { } nameError)
        {
            return new DockerActionResult { Succeeded = false, Message = nameError };
        }

        // Validate before writing: a bad file must never replace a good one.
        var validation = await ValidateAsync(yaml, cancellationToken).ConfigureAwait(false);

        if (!validation.Succeeded)
        {
            return validation;
        }

        try
        {
            Directory.CreateDirectory(DirectoryFor(projectName));

            await systemRunner
                .WriteFileAsync(ComposeFileFor(projectName), yaml, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Saved compose project {ProjectName}.", projectName);

            return new DockerActionResult { Succeeded = true, Message = $"Saved {projectName}." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemOperationException)
        {
            return new DockerActionResult { Succeeded = false, Message = $"Could not save: {ex.Message}" };
        }
    }

    /// <summary>Brings a project up, pulling images as needed.</summary>
    public Task<DockerActionResult> UpAsync(ComposeProject project, CancellationToken cancellationToken = default) =>
        RunComposeAsync(project, ["up", "--detach", "--remove-orphans"], "start", options.ComposeUpTimeout, cancellationToken);

    /// <summary>Stops a project and removes its containers and networks. Volumes are kept.</summary>
    public Task<DockerActionResult> DownAsync(
        ComposeProject project,
        bool removeVolumes = false,
        CancellationToken cancellationToken = default) =>
        RunComposeAsync(
            project,
            removeVolumes ? ["down", "--volumes"] : ["down"],
            "stop and remove",
            options.ComposeUpTimeout,
            cancellationToken);

    /// <summary>Restarts a project's containers without recreating them.</summary>
    public Task<DockerActionResult> RestartAsync(ComposeProject project, CancellationToken cancellationToken = default) =>
        RunComposeAsync(project, ["restart"], "restart", options.ComposeUpTimeout, cancellationToken);

    /// <summary>Stops a project's containers, leaving them in place.</summary>
    public Task<DockerActionResult> StopAsync(ComposeProject project, CancellationToken cancellationToken = default) =>
        RunComposeAsync(project, ["stop"], "stop", options.ComposeUpTimeout, cancellationToken);

    /// <summary>Pulls newer images for a project.</summary>
    public Task<DockerActionResult> PullAsync(ComposeProject project, CancellationToken cancellationToken = default) =>
        RunComposeAsync(project, ["pull"], "pull images for", options.ComposeUpTimeout, cancellationToken);

    /// <summary>
    /// Takes a project down and deletes its compose file, removing it entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="DownAsync"/>, which only removes the containers: the file stays,
    /// so the project is rediscovered on the next scan and sits in the list as "not created"
    /// forever. This is what actually removes it.
    /// </para>
    /// <para>
    /// Only for managed projects. An external project's file lives somewhere this module does not
    /// own — someone's home directory, a git checkout — and deleting it would be well beyond what
    /// "remove this from the list" ought to mean.
    /// </para>
    /// </remarks>
    public async Task<DockerActionResult> DeleteAsync(
        ComposeProject project,
        bool removeVolumes = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!project.IsManaged)
        {
            return new DockerActionResult
            {
                Succeeded = false,
                Message = $"'{project.Name}' is defined outside {options.ComposeProjectsPath}, so its file is not "
                          + "this tool's to delete. Take it down here and remove the file yourself.",
            };
        }

        // Containers first: once the file is gone compose cannot work out what to remove, and the
        // containers would be orphaned with nothing left describing them.
        if (project.IsActionable && project.ContainerCount > 0)
        {
            var down = await DownAsync(project, removeVolumes, cancellationToken).ConfigureAwait(false);

            if (!down.Succeeded)
            {
                return new DockerActionResult
                {
                    Succeeded = false,
                    Message = $"Could not take '{project.Name}' down, so its files were left alone: {down.Message}",
                };
            }
        }

        var directory = DirectoryFor(project.Name);

        // The name is validated on the way in, but this is a recursive delete: check the resolved
        // path really is inside the projects directory rather than trusting that it must be.
        var root = Path.GetFullPath(options.ComposeProjectsPath);
        var resolved = Path.GetFullPath(directory);

        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            logger.LogError("Refusing to delete {Resolved}: it is outside {Root}.", resolved, root);

            return new DockerActionResult
            {
                Succeeded = false,
                Message = $"Refusing to delete '{resolved}': it is outside the projects directory.",
            };
        }

        try
        {
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }

            logger.LogInformation("Deleted compose project {ProjectName} ({Directory}).", project.Name, resolved);

            return new DockerActionResult { Succeeded = true, Message = $"Deleted {project.Name}." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DockerActionResult
            {
                Succeeded = false,
                Message = $"'{project.Name}' was taken down, but its files could not be deleted: {ex.Message}",
            };
        }
    }

    private async Task<DockerActionResult> RunComposeAsync(
        ComposeProject project,
        string[] verb,
        string description,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!project.IsActionable)
        {
            return new DockerActionResult
            {
                Succeeded = false,
                Message = $"No compose file is known for '{project.Name}', so it cannot be managed from here.",
            };
        }

        var arguments = new List<string> { "compose", "--project-name", project.Name };

        // --project-directory rather than changing directory: ISystemRunner deliberately has no
        // working-directory concept, and relative paths inside the file must still resolve
        // against the file's own location.
        if (project.WorkingDirectory is { Length: > 0 } workingDirectory)
        {
            arguments.Add("--project-directory");
            arguments.Add(workingDirectory);
        }

        foreach (var file in project.ConfigFiles)
        {
            arguments.Add("--file");
            arguments.Add(file);
        }

        arguments.AddRange(verb);

        logger.LogInformation("docker {Arguments}", string.Join(' ', arguments));

        try
        {
            // The long timeout is the point: `up` pulls images, and being killed at 30 seconds
            // leaves a half-pulled layer and a project that never starts.
            var result = await systemRunner
                .RunAsync(Executable, arguments, timeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                return new DockerActionResult { Succeeded = true, Message = $"Did {description} {project.Name}." };
            }

            // Compose writes its progress and its errors to stderr, so the last line is the
            // useful one rather than the whole transcript.
            var error = result.StandardError.Trim();
            var lastLine = error.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();

            logger.LogWarning("Could not {Description} {ProjectName}: {Error}", description, project.Name, error);

            return new DockerActionResult
            {
                Succeeded = false,
                Message = lastLine is { Length: > 0 }
                    ? lastLine
                    : $"Could not {description} {project.Name}: exit code {result.ExitCode}.",
            };
        }
        catch (SystemOperationException ex)
        {
            logger.LogError(ex, "Could not {Description} {ProjectName}.", description, project.Name);

            return new DockerActionResult { Succeeded = false, Message = ex.Message };
        }
    }
}
