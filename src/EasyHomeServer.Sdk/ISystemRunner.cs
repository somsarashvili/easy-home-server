namespace EasyHomeServer.Sdk;

/// <summary>
/// The single seam through which modules touch the operating system: running processes,
/// reading and writing system configuration files, and driving systemd.
/// </summary>
/// <remarks>
/// <para>
/// Module code must never call <c>Process.Start</c> or <c>System.IO.File</c> against system
/// paths directly. Today the host implements this in-process; keeping every privileged
/// operation behind this interface means a future split into a separate privileged worker
/// (with the web process dropped to an unprivileged user) requires no module changes.
/// </para>
/// <para>
/// Arguments are passed as a list and never concatenated into a shell command line, so
/// values containing spaces or shell metacharacters cannot be reinterpreted as syntax.
/// There is deliberately no "run this string in a shell" overload.
/// </para>
/// </remarks>
public interface ISystemRunner
{
    /// <summary>
    /// Runs an executable to completion and captures its output. Does not use a shell.
    /// </summary>
    /// <param name="fileName">Executable to run; resolved against PATH if not absolute.</param>
    /// <param name="arguments">Arguments passed individually, without shell interpretation.</param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a system file. Throws <see cref="SystemOperationException"/> if unreadable.</summary>
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a system file atomically (write to a temporary file in the same directory,
    /// then rename), so a crash mid-write cannot leave a half-written config behind.
    /// </summary>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes systemctl. <paramref name="unit"/> is required for unit-scoped actions and
    /// must be null for system-scoped ones (<see cref="SystemctlAction.Reboot"/>,
    /// <see cref="SystemctlAction.PowerOff"/>).
    /// </summary>
    Task<ProcessResult> SystemctlAsync(
        SystemctlAction action,
        string? unit = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a process launched through <see cref="ISystemRunner"/>.</summary>
public sealed record ProcessResult
{
    /// <summary>Process exit code. Zero conventionally means success.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Captured standard error.</summary>
    public required string StandardError { get; init; }

    /// <summary>True when <see cref="ExitCode"/> is zero.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>systemd operations exposed to modules.</summary>
public enum SystemctlAction
{
    /// <summary>Start a unit.</summary>
    Start,

    /// <summary>Stop a unit.</summary>
    Stop,

    /// <summary>Restart a unit.</summary>
    Restart,

    /// <summary>Ask a unit to reload its configuration.</summary>
    Reload,

    /// <summary>Enable a unit at boot.</summary>
    Enable,

    /// <summary>Disable a unit at boot.</summary>
    Disable,

    /// <summary>Query a unit's status.</summary>
    Status,

    /// <summary>Reboot the machine. Takes no unit.</summary>
    Reboot,

    /// <summary>Power the machine off. Takes no unit.</summary>
    PowerOff,
}

/// <summary>Thrown when a privileged operation cannot be carried out.</summary>
public sealed class SystemOperationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public SystemOperationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and underlying cause.</summary>
    public SystemOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
