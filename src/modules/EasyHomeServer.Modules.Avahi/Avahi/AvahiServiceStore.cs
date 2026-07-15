using System.Collections.Immutable;
using System.Text.RegularExpressions;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Avahi.Avahi;

/// <summary>
/// Owns this module's service files in avahi's services directory.
/// </summary>
/// <remarks>
/// <para>
/// avahi watches that directory and republishes within a second or so of a file appearing,
/// changing or being deleted. There is no daemon reload to trigger and no restart to schedule:
/// writing the file <em>is</em> the API.
/// </para>
/// <para>
/// The directory is shared with the distribution and the admin — a stock install already has
/// sftp-ssh.service and udisks.service in it. So this class only ever reads, writes or deletes
/// files matching its own prefix. Reconciliation deletes "everything not desired", and without
/// that rule the first reconcile would wipe the machine's other advertisements.
/// </para>
/// </remarks>
public sealed partial class AvahiServiceStore(ISystemRunner systemRunner, AvahiOptions options, ILogger<AvahiServiceStore> logger)
{
    /// <summary>
    /// Prefix marking a file as ours. Anything without it belongs to someone else and is never
    /// written or deleted.
    /// </summary>
    private const string OwnedPrefix = "easyhomeserver-";

    private const string Extension = ".service";

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex UnsafeFileNameChars { get; }

    /// <summary>Full path of the file backing a given advertisement key.</summary>
    public string PathFor(string key) => Path.Combine(options.ServicesPath, $"{OwnedPrefix}{Sanitise(key)}{Extension}");

    /// <summary>
    /// Reduces a key to something safe as a filename. Container names allow characters that a
    /// path does not, and a key is never trusted to be one.
    /// </summary>
    private static string Sanitise(string key)
    {
        var lowered = key.ToLowerInvariant();
        var cleaned = UnsafeFileNameChars.Replace(lowered, "-").Trim('-');

        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }

    /// <summary>Lists the service files this module currently owns.</summary>
    public ImmutableArray<string> ListOwnedFiles()
    {
        try
        {
            if (!Directory.Exists(options.ServicesPath))
            {
                return [];
            }

            return
            [
                .. Directory
                    .GetFiles(options.ServicesPath, $"{OwnedPrefix}*{Extension}", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.Ordinal),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list {ServicesPath}.", options.ServicesPath);

            return [];
        }
    }

    /// <summary>
    /// Makes the advertised set match <paramref name="desired"/>: writes what is new or changed,
    /// deletes what is no longer wanted, and leaves the rest alone.
    /// </summary>
    /// <remarks>
    /// Unchanged files are compared and skipped rather than rewritten. This runs on every Docker
    /// inventory — every few seconds — and rewriting an identical file would make avahi
    /// republish, which briefly withdraws and re-announces the service. Browsers would see every
    /// service flicker continuously.
    /// </remarks>
    public async Task<ReconcileResult> ReconcileAsync(
        IReadOnlyCollection<ServiceDefinition> desired,
        CancellationToken cancellationToken = default)
    {
        var written = 0;
        var removed = 0;
        var unchanged = 0;

        var desiredPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var service in desired)
        {
            var path = PathFor(service.Key);
            desiredPaths.Add(path);

            var content = service.ToServiceFile();

            try
            {
                if (File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) == content)
                {
                    unchanged++;

                    continue;
                }

                await systemRunner.WriteFileAsync(path, content, cancellationToken).ConfigureAwait(false);
                written++;

                logger.LogInformation(
                    "Advertising {DisplayName} ({ServiceType} on port {Port}) from {Path}.",
                    service.DisplayName,
                    service.ServiceType,
                    service.Port,
                    path);
            }
            catch (SystemOperationException ex)
            {
                logger.LogError(ex, "Could not write the service file for {Key}.", service.Key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not read the existing service file {Path}.", path);
            }
        }

        // Only ever considers files carrying our prefix; the admin's own advertisements are not
        // ours to delete.
        foreach (var path in ListOwnedFiles())
        {
            if (desiredPaths.Contains(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;

                logger.LogInformation("Withdrew the advertisement backed by {Path}.", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not remove the stale service file {Path}.", path);
            }
        }

        return new ReconcileResult { Written = written, Removed = removed, Unchanged = unchanged };
    }

    /// <summary>Removes every file this module owns. Used when advertising is switched off.</summary>
    public Task<ReconcileResult> WithdrawAllAsync(CancellationToken cancellationToken = default) =>
        ReconcileAsync([], cancellationToken);
}

/// <summary>What a reconcile did.</summary>
public sealed record ReconcileResult
{
    /// <summary>Files created or updated.</summary>
    public required int Written { get; init; }

    /// <summary>Files deleted.</summary>
    public required int Removed { get; init; }

    /// <summary>Files already correct and left alone.</summary>
    public required int Unchanged { get; init; }

    /// <summary>True when the reconcile changed nothing — the steady state.</summary>
    public bool NoChanges => Written == 0 && Removed == 0;
}
