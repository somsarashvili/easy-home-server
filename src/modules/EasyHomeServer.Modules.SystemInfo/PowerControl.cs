using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.SystemInfo;

/// <summary>
/// Reboot and shutdown, driven entirely through <see cref="ISystemRunner"/>.
/// </summary>
/// <remarks>
/// This is the module's proof that the privileged seam works: there is no
/// <c>Process.Start</c> here, no <c>/sbin/reboot</c> path, nothing platform-specific. When the
/// host later moves privileged work into a separate worker process, this class is unchanged.
/// </remarks>
public sealed class PowerControl(ISystemRunner systemRunner, ILogger<PowerControl> logger)
{
    /// <summary>Reboots the machine.</summary>
    public Task<PowerActionResult> RebootAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(SystemctlAction.Reboot, "reboot", cancellationToken);
    }

    /// <summary>Powers the machine off.</summary>
    public Task<PowerActionResult> ShutDownAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(SystemctlAction.PowerOff, "shut down", cancellationToken);
    }

    private async Task<PowerActionResult> InvokeAsync(
        SystemctlAction action,
        string description,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Admin requested {Description} of this machine.", description);

        try
        {
            var result = await systemRunner.SystemctlAsync(action, cancellationToken: cancellationToken);

            if (result.Succeeded)
            {
                return new PowerActionResult
                {
                    Succeeded = true,
                    Message = $"The server is about to {description}. This page will disconnect.",
                };
            }

            var detail = result.StandardError.Trim();

            return new PowerActionResult
            {
                Succeeded = false,
                Message = detail.Length > 0
                    ? $"Could not {description}: {detail}"
                    : $"Could not {description}: systemctl exited with code {result.ExitCode}.",
            };
        }
        catch (SystemOperationException ex)
        {
            logger.LogError(ex, "Failed to {Description} the machine.", description);

            return new PowerActionResult { Succeeded = false, Message = $"Could not {description}: {ex.Message}" };
        }
    }
}

/// <summary>Outcome of a power action, phrased for display.</summary>
public sealed record PowerActionResult
{
    /// <summary>Whether the request was accepted by systemd.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Message to show the operator.</summary>
    public required string Message { get; init; }
}
