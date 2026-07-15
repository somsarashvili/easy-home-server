using System.Collections.Immutable;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.SnapRaid;

/// <summary>How a read of the array turned out.</summary>
public enum SnapRaidOutcome
{
    /// <summary>The array was read.</summary>
    Ok,

    /// <summary>SnapRAID is doing something else and would not answer.</summary>
    Busy,

    /// <summary>There is no snapraid.conf, so this machine has no array.</summary>
    NotConfigured,

    /// <summary>snapraid is not installed.</summary>
    NotInstalled,

    /// <summary>It ran and failed.</summary>
    Failed,
}

/// <summary>The result of reading the array.</summary>
public sealed record SnapRaidReading
{
    /// <summary>How it turned out.</summary>
    public required SnapRaidOutcome Outcome { get; init; }

    /// <summary>The report, when there is one.</summary>
    public SnapRaidStatus? Status { get; init; }

    /// <summary>The array as configured, when snapraid.conf could be read.</summary>
    public SnapRaidConfig? Config { get; init; }

    /// <summary>What went wrong, for the outcomes where something did.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Runs snapraid.
/// </summary>
/// <remarks>
/// Every call has to expect <see cref="SnapRaidOutcome.Busy"/>. SnapRAID takes a lock around the
/// whole array, and the machine this targets syncs from cron every hour over several terabytes of
/// SMR disk, so a status read landing mid-sync is ordinary rather than exceptional. Reporting that
/// as a failure would cry wolf once an hour.
/// </remarks>
public sealed class SnapRaidCli(ISystemRunner systemRunner, DisksOptions options, ILogger<SnapRaidCli> logger)
{
    private const string Executable = "snapraid";

    /// <summary>What a process's exit code is when something SIGTERMs it: 128 plus the signal.</summary>
    private const int SigTermExitCode = 143;

    /// <summary>
    /// How long to let status run.
    /// </summary>
    /// <remarks>
    /// Generous: status reads the whole content file, which on the real array is a few hundred MiB
    /// and takes tens of seconds from cold cache.
    /// </remarks>
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Whether snapraid is installed.</summary>
    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await systemRunner
                .RunAsync(Executable, ["--version"], cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded;
        }
        catch (SystemOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a snapraid is running right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asking the process table rather than running <c>snapraid status</c> and reading its refusal:
    /// status loads the whole content file, which is a few hundred MiB on the real array, and this
    /// is asked before an action rather than on a poll. It also answers when status could not.
    /// </para>
    /// <para>
    /// <c>-x</c> matches the process name exactly, so it cannot match this service's own command
    /// line the way a <c>-f</c> pattern search would.
    /// </para>
    /// </remarks>
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await systemRunner
                .RunAsync("pgrep", ["-x", Executable], cancellationToken)
                .ConfigureAwait(false);

            // pgrep exits 0 when it matched something, 1 when it did not.
            return result.ExitCode == 0;
        }
        catch (SystemOperationException ex)
        {
            logger.LogWarning(ex, "Could not check whether snapraid is running; assuming it is, to be safe.");

            // Refusing an action wrongly is recoverable; running one over a live sync is not.
            return true;
        }
    }

    /// <summary>Reads the array: its configuration, and what SnapRAID says about it.</summary>
    public async Task<SnapRaidReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SnapRaidReading { Outcome = SnapRaidOutcome.NotInstalled };
        }

        var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);

        if (config is null)
        {
            return new SnapRaidReading { Outcome = SnapRaidOutcome.NotConfigured };
        }

        try
        {
            var result = await systemRunner
                .RunAsync(Executable, ["status"], StatusTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                return new SnapRaidReading
                {
                    Outcome = SnapRaidOutcome.Ok,
                    Config = config,
                    Status = SnapRaidStatusParser.Parse(result.StandardOutput, DateTimeOffset.UtcNow),
                };
            }

            var combined = result.StandardOutput + result.StandardError;

            if (IsLocked(combined))
            {
                logger.LogDebug("snapraid is busy; status will be read again on the next tick.");

                return new SnapRaidReading { Outcome = SnapRaidOutcome.Busy, Config = config };
            }

            // Killed rather than failed: the service is stopping and took its status read with it.
            // Warning about that on every restart would train the reader to skip these lines.
            if (cancellationToken.IsCancellationRequested || result.ExitCode == SigTermExitCode)
            {
                return new SnapRaidReading { Outcome = SnapRaidOutcome.Busy, Config = config };
            }

            logger.LogWarning("snapraid status failed: {Error}", result.StandardError.Trim());

            return new SnapRaidReading
            {
                Outcome = SnapRaidOutcome.Failed,
                Config = config,
                Message = FirstMeaningfulLine(combined),
            };
        }
        catch (SystemOperationException ex)
        {
            logger.LogError(ex, "Could not run snapraid status.");

            return new SnapRaidReading
            {
                Outcome = SnapRaidOutcome.Failed,
                Config = config,
                Message = ex.Message,
            };
        }
    }

    /// <summary>
    /// Whether the output is SnapRAID refusing because it is already running.
    /// </summary>
    /// <remarks>
    /// Matched on the wording because SnapRAID exits 1 for this exactly as it does for a real
    /// fault. Checking the lock file's existence instead would be worse: it is left behind by a
    /// killed process, and would report a dead sync as a live one forever.
    /// </remarks>
    private static bool IsLocked(string output) =>
        output.Contains("already in use", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads and parses snapraid.conf, or null when there is not one.</summary>
    public async Task<SnapRaidConfig?> ReadConfigAsync(CancellationToken cancellationToken = default)
    {
        string content;

        try
        {
            content = await systemRunner.ReadFileAsync(options.SnapRaidConfigPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "No snapraid config at {Path}.", options.SnapRaidConfigPath);

            return null;
        }

        return ParseConfig(content);
    }

    /// <summary>
    /// Parses snapraid.conf.
    /// </summary>
    /// <remarks>
    /// The format is one directive per line, name then value, with # for comments. Unknown
    /// directives are skipped rather than rejected: the file has options this module has no
    /// opinion about, and refusing to show an array because of one would help nobody.
    /// </remarks>
    private static SnapRaidConfig ParseConfig(string content)
    {
        var parity = ImmutableArray.CreateBuilder<string>();
        var contentFiles = ImmutableArray.CreateBuilder<string>();
        var data = ImmutableArray.CreateBuilder<SnapRaidDataDisk>();
        var excludes = ImmutableArray.CreateBuilder<string>();

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);

            if (parts.Length < 2)
            {
                continue;
            }

            var (directive, value) = (parts[0].ToLowerInvariant(), parts[1]);

            switch (directive)
            {
                // parity, 2-parity, 3-parity... each names one more disk that may fail.
                case "parity":
                case var _ when directive.EndsWith("-parity", StringComparison.Ordinal):
                    parity.Add(value);

                    break;

                case "content":
                    contentFiles.Add(value);

                    break;

                case "data":
                    if (ParseDataDisk(value) is { } disk)
                    {
                        data.Add(disk);
                    }

                    break;

                case "exclude":
                    excludes.Add(value);

                    break;
            }
        }

        return new SnapRaidConfig
        {
            ParityFiles = parity.ToImmutable(),
            ContentFiles = contentFiles.ToImmutable(),
            DataDisks = data.ToImmutable(),
            Excludes = excludes.ToImmutable(),
        };
    }

    /// <summary>Parses a data line's value, which is a name then a path: <c>d1 /mnt/data1/</c>.</summary>
    private static SnapRaidDataDisk? ParseDataDisk(string value)
    {
        var parts = value.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return null;
        }

        return new SnapRaidDataDisk
        {
            Name = parts[0],
            // Trailing slash is how snapraid.conf writes these; paths elsewhere have none, and
            // the two are compared to match a disk to its mount.
            Path = parts[1].TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : parts[1],
        };
    }

    private static string? FirstMeaningfulLine(string output) =>
        output.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0
                                    && !line.StartsWith("Self test", StringComparison.OrdinalIgnoreCase)
                                    && !line.StartsWith("Loading state", StringComparison.OrdinalIgnoreCase));
}
