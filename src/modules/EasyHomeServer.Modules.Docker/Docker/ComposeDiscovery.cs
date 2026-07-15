using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// Works out which Compose projects exist, by merging what the containers say with what is on
/// disk.
/// </summary>
public sealed class ComposeDiscovery(ComposeCli compose)
{
    /// <summary>Label compose stamps on every container with the project name.</summary>
    public const string ProjectLabel = "com.docker.compose.project";

    /// <summary>Label carrying the service name within the project.</summary>
    public const string ServiceLabel = "com.docker.compose.service";

    /// <summary>Label carrying the directory the project was brought up from.</summary>
    public const string WorkingDirLabel = "com.docker.compose.project.working_dir";

    /// <summary>Label carrying the compose files used, comma-separated.</summary>
    public const string ConfigFilesLabel = "com.docker.compose.project.config_files";

    /// <summary>
    /// Builds the project list from a set of containers plus the stacks directory.
    /// </summary>
    public ImmutableArray<ComposeProject> Discover(ImmutableArray<DockerContainer> containers)
    {
        var projects = new Dictionary<string, ComposeProject>(StringComparer.Ordinal);

        // From containers: every project that has ever been brought up, wherever its file lives.
        var byProject = containers
            .Where(c => c.Labels.ContainsKey(ProjectLabel))
            .GroupBy(c => c.Labels[ProjectLabel], StringComparer.Ordinal);

        foreach (var group in byProject)
        {
            var sample = group.First();

            var services = group
                .GroupBy(
                    c => c.Labels.TryGetValue(ServiceLabel, out var service) ? service : c.Name,
                    StringComparer.Ordinal)
                .Select(g => new ComposeService
                {
                    Name = g.Key,
                    Containers = [.. g.OrderBy(c => c.Name, StringComparer.Ordinal)],
                })
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToImmutableArray();

            projects[group.Key] = new ComposeProject
            {
                Name = group.Key,
                WorkingDirectory = sample.Labels.TryGetValue(WorkingDirLabel, out var dir) ? dir : null,
                ConfigFiles = ParseConfigFiles(sample),
                Services = services,
                IsManaged = false,
            };
        }

        // From disk: projects that are fully down have no containers and are invisible above.
        foreach (var (name, configFile) in compose.ScanProjectsDirectory())
        {
            if (projects.TryGetValue(name, out var existing))
            {
                // Already found via containers. Mark it managed so the UI offers editing, and
                // trust the labels for the file path — the project may have been brought up from
                // a different file than the one now sitting in the directory.
                projects[name] = existing with
                {
                    IsManaged = true,
                    ConfigFiles = existing.ConfigFiles.Length > 0 ? existing.ConfigFiles : [configFile],
                    WorkingDirectory = existing.WorkingDirectory ?? Path.GetDirectoryName(configFile),
                };

                continue;
            }

            projects[name] = new ComposeProject
            {
                Name = name,
                WorkingDirectory = Path.GetDirectoryName(configFile),
                ConfigFiles = [configFile],
                Services = [],
                IsManaged = true,
            };
        }

        return [.. projects.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads the config-files label, which compose writes as a comma-separated list of absolute
    /// paths — one entry per <c>-f</c> given when the project was brought up.
    /// </summary>
    private static ImmutableArray<string> ParseConfigFiles(DockerContainer container)
    {
        if (!container.Labels.TryGetValue(ConfigFilesLabel, out var value) || value.Length == 0)
        {
            return [];
        }

        return [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
