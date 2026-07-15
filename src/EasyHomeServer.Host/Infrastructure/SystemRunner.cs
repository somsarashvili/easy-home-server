using System.Diagnostics;
using System.Text;
using EasyHomeServer.Sdk;

namespace EasyHomeServer.Host.Infrastructure;

/// <summary>
/// In-process implementation of <see cref="ISystemRunner"/>. Runs as whatever user the
/// systemd unit specifies (root today).
/// </summary>
/// <remarks>
/// Everything privileged funnels through here, which is the point: when the privileged
/// worker split happens, this class becomes an IPC client to that worker and no module has
/// to change. Keep it free of module-specific logic.
/// </remarks>
internal sealed class SystemRunner(ILogger<SystemRunner> logger) : ISystemRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        RunAsync(fileName, arguments, DefaultTimeout, cancellationToken);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList quotes each value for the platform; the command line is never built by
        // string concatenation, so arguments cannot be reinterpreted as shell syntax.
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        logger.LogDebug("Running {FileName} {Arguments}", fileName, string.Join(' ', arguments));

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new SystemOperationException($"Could not start '{fileName}': {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process, fileName);

            throw new SystemOperationException(
                $"'{fileName}' did not exit within {timeout.TotalSeconds:0} seconds and was terminated.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process, fileName);

            throw;
        }

        var result = new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "{FileName} exited with code {ExitCode}. stderr: {StandardError}",
                fileName,
                result.ExitCode,
                result.StandardError.Trim());
        }

        return result;
    }

    private void TryKill(Process process, string fileName)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not terminate '{FileName}' after cancellation.", fileName);
        }
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SystemOperationException($"Could not read '{path}': {ex.Message}", ex);
        }
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directory))
        {
            throw new SystemOperationException($"'{path}' is not an absolute file path.");
        }

        // Write-then-rename: the temporary file must share a filesystem with the target for
        // the rename to be atomic, so it goes in the same directory.
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);

            logger.LogInformation("Wrote system file {Path} ({ByteCount} bytes).", path, content.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SystemOperationException($"Could not write '{path}': {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not clean up temporary file {TemporaryPath}.", temporaryPath);
                }
            }
        }
    }

    public Task<ProcessResult> SystemctlAsync(
        SystemctlAction action,
        string? unit = null,
        CancellationToken cancellationToken = default)
    {
        var verb = action switch
        {
            SystemctlAction.Start => "start",
            SystemctlAction.Stop => "stop",
            SystemctlAction.Restart => "restart",
            SystemctlAction.Reload => "reload",
            SystemctlAction.Enable => "enable",
            SystemctlAction.Disable => "disable",
            SystemctlAction.Status => "status",
            SystemctlAction.Reboot => "reboot",
            SystemctlAction.PowerOff => "poweroff",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported systemctl action."),
        };

        var isSystemScoped = action is SystemctlAction.Reboot or SystemctlAction.PowerOff;

        if (isSystemScoped && !string.IsNullOrEmpty(unit))
        {
            throw new ArgumentException($"'{verb}' does not take a unit.", nameof(unit));
        }

        if (!isSystemScoped && string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException($"'{verb}' requires a unit name.", nameof(unit));
        }

        logger.LogInformation("systemctl {Verb} {Unit}", verb, unit ?? string.Empty);

        var arguments = isSystemScoped ? new[] { verb } : [verb, unit!];

        return RunAsync("systemctl", arguments, cancellationToken);
    }
}
