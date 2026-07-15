using System.Collections.Immutable;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// A Compose project: a set of services defined by one or more compose files.
/// </summary>
/// <remarks>
/// Assembled from two sources that each see only half the picture. Containers carry
/// <c>com.docker.compose.*</c> labels, which reveal every project that has ever been brought up —
/// but a project that is fully <c>down</c> has no containers and is invisible that way. Scanning
/// a stacks directory finds those, but not projects living elsewhere on disk. Merging both means
/// the list matches what an operator believes exists.
/// </remarks>
public sealed record ComposeProject
{
    /// <summary>Project name, as passed to <c>--project-name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Directory the compose file lives in, used as <c>--project-directory</c> so relative
    /// bind-mount paths in the file resolve the way they did when it was written.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Compose files making up the project, in order.</summary>
    public ImmutableArray<string> ConfigFiles { get; init; } = [];

    /// <summary>Services in the project, each with its containers.</summary>
    public ImmutableArray<ComposeService> Services { get; init; } = [];

    /// <summary>
    /// True when the project's file lives in the stacks directory this module manages, and so can
    /// be edited here. A project discovered only from labels is someone else's file, in an
    /// arbitrary place, and is shown but not edited.
    /// </summary>
    public required bool IsManaged { get; init; }

    /// <summary>All containers across all services.</summary>
    public IEnumerable<DockerContainer> Containers => Services.SelectMany(s => s.Containers);

    /// <summary>Containers currently running.</summary>
    public int RunningCount => Containers.Count(c => c.IsRunning);

    /// <summary>Total containers created for this project.</summary>
    public int ContainerCount => Containers.Count();

    /// <summary>Overall state, from its containers.</summary>
    public ComposeProjectStatus Status => this switch
    {
        { ContainerCount: 0 } => ComposeProjectStatus.NotCreated,
        _ when RunningCount == 0 => ComposeProjectStatus.Stopped,
        _ when RunningCount == ContainerCount => ComposeProjectStatus.Running,
        _ => ComposeProjectStatus.Partial,
    };

    /// <summary>True when compose can act on it — there is a file to act on.</summary>
    public bool IsActionable => ConfigFiles.Length > 0;
}

/// <summary>One service within a project, and the containers realising it.</summary>
public sealed record ComposeService
{
    /// <summary>Service name from the compose file.</summary>
    public required string Name { get; init; }

    /// <summary>Containers created for this service. More than one when scaled.</summary>
    public ImmutableArray<DockerContainer> Containers { get; init; } = [];

    /// <summary>Containers of this service that are running.</summary>
    public int RunningCount => Containers.Count(c => c.IsRunning);

    /// <summary>Image the service runs, taken from its first container.</summary>
    public string Image => Containers.Length > 0 ? Containers[0].Image : "(not created)";
}

/// <summary>Overall state of a Compose project.</summary>
public enum ComposeProjectStatus
{
    /// <summary>A compose file exists but no containers have been created.</summary>
    NotCreated,

    /// <summary>Containers exist; none are running.</summary>
    Stopped,

    /// <summary>Some containers are running, some are not.</summary>
    Partial,

    /// <summary>Every container is running.</summary>
    Running,
}
