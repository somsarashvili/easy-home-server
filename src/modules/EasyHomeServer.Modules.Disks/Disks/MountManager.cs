using System.Text;
using System.Text.RegularExpressions;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>
/// Mounts and unmounts filesystems, and records the ones that should come back at boot.
/// </summary>
/// <remarks>
/// <para>
/// Every mount goes through PID 1's mount namespace, and this is not optional. The unit sets
/// <c>PrivateTmp=</c>, which puts this service in its own namespace — so a plain <c>mount</c> here
/// succeeds, is visible to this process, and is invisible to everything else on the machine.
/// </para>
/// <para>
/// That failure is silent and it is severe. The page would show the disk mounted at /srv/media;
/// the shell, Docker and Samba would all see an empty directory sitting on the root filesystem.
/// Anything written there fills <c>/</c> while the disk it was supposed to land on stays empty,
/// and nobody finds out until the root filesystem is full.
/// </para>
/// </remarks>
public sealed partial class MountManager(ISystemRunner systemRunner, DisksOptions options, ILogger<MountManager> logger)
{
    private const string BeginMarker = "# BEGIN EasyHomeServer — managed automatically, do not edit";
    private const string EndMarker = "# END EasyHomeServer";

    /// <summary>
    /// Mount points this module will create under. Anywhere else has to already exist.
    /// </summary>
    /// <remarks>
    /// Creating a directory anywhere on request is how you end up with a filesystem mounted over
    /// <c>/usr</c>. Under a mount root it is inert.
    /// </remarks>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9 ._-]*$")]
    private static partial Regex MountNamePattern { get; }

    /// <summary>Result of a mount operation, phrased for display.</summary>
    public sealed record MountResult
    {
        /// <summary>Whether it worked.</summary>
        public required bool Succeeded { get; init; }

        /// <summary>What happened, in words.</summary>
        public required string Message { get; init; }
    }

    /// <summary>The default place to mount a device, from its label or its name.</summary>
    public string SuggestMountPoint(BlockDevice device)
    {
        var name = device.Label is { Length: > 0 } label ? label : device.Name;
        var cleaned = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')])
            .Trim('-');

        return System.IO.Path.Combine(options.MountRoot, cleaned.Length > 0 ? cleaned : device.Name);
    }

    /// <summary>
    /// Mounts a device.
    /// </summary>
    /// <remarks>
    /// Mounts by UUID rather than by path. Kernel names are assigned in discovery order, so the
    /// disk that is /dev/sdb today can be /dev/sdc after a reboot or a USB replug — mounting the
    /// wrong disk at the right path is a genuinely bad outcome. A UUID belongs to the filesystem.
    /// </remarks>
    public async Task<MountResult> MountAsync(
        BlockDevice device,
        string mountPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (Validate(device, mountPoint) is { } error)
        {
            return new MountResult { Succeeded = false, Message = error };
        }

        try
        {
            Directory.CreateDirectory(mountPoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MountResult { Succeeded = false, Message = $"Could not create '{mountPoint}': {ex.Message}" };
        }

        var source = device.Uuid is { Length: > 0 } uuid ? $"UUID={uuid}" : device.Path;

        try
        {
            var result = await RunInHostNamespaceAsync("mount", [source, mountPoint], cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                logger.LogInformation("Mounted {Device} at {MountPoint}.", device.Path, mountPoint);

                return new MountResult { Succeeded = true, Message = $"Mounted {device.Name} at {mountPoint}." };
            }

            return new MountResult
            {
                Succeeded = false,
                Message = result.StandardError.Trim() is { Length: > 0 } detail
                    ? detail
                    : $"mount exited with code {result.ExitCode}.",
            };
        }
        catch (SystemOperationException ex)
        {
            return new MountResult { Succeeded = false, Message = ex.Message };
        }
    }

    /// <summary>Unmounts a device.</summary>
    public async Task<MountResult> UnmountAsync(BlockDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.MountPoint is not { } mountPoint)
        {
            return new MountResult { Succeeded = false, Message = $"{device.Name} is not mounted." };
        }

        if (device.IsSystemDisk)
        {
            return new MountResult
            {
                Succeeded = false,
                Message = $"{mountPoint} is part of the running system and cannot be unmounted.",
            };
        }

        try
        {
            var result = await RunInHostNamespaceAsync("umount", [mountPoint], cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                logger.LogInformation("Unmounted {MountPoint}.", mountPoint);

                return new MountResult { Succeeded = true, Message = $"Unmounted {mountPoint}." };
            }

            var error = result.StandardError.Trim();

            // The overwhelmingly common failure, and its message ("target is busy") does not say
            // how to find out what is holding it.
            if (error.Contains("busy", StringComparison.OrdinalIgnoreCase))
            {
                return new MountResult
                {
                    Succeeded = false,
                    Message = $"{mountPoint} is in use — something has a file open there, or a container is "
                              + $"mounting it. Find out with: lsof +f -- {mountPoint}",
                };
            }

            return new MountResult { Succeeded = false, Message = error.Length > 0 ? error : "umount failed." };
        }
        catch (SystemOperationException ex)
        {
            return new MountResult { Succeeded = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Runs a command in PID 1's mount namespace, so its mounts are the machine's.
    /// </summary>
    /// <remarks>
    /// Falls back to running plainly only when nsenter is unavailable — in a container, where this
    /// process may be PID 1 and there is no namespace to escape. Everywhere else, running plainly
    /// would be the silent-failure described on the class.
    /// </remarks>
    private async Task<ProcessResult> RunInHostNamespaceAsync(
        string file,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await systemRunner
                .RunAsync("nsenter", ["--target", "1", "--mount", file, .. arguments], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SystemOperationException)
        {
            logger.LogWarning(
                "nsenter is unavailable, so {File} runs in this service's mount namespace. If this service is "
                    + "sandboxed, the result will not be visible to the rest of the machine.",
                file);

            return await systemRunner.RunAsync(file, arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    private string? Validate(BlockDevice device, string mountPoint)
    {
        if (device.FileSystem is null)
        {
            return $"{device.Name} has no filesystem on it. Format it first.";
        }

        if (device.IsMounted)
        {
            return $"{device.Name} is already mounted at {device.MountPoint}.";
        }

        if (!mountPoint.StartsWith('/'))
        {
            return "The mount point must be an absolute path.";
        }

        // Mounting over a system directory hides everything under it for as long as the mount
        // lasts, and the machine generally stops working.
        var forbidden = new[] { "/", "/usr", "/etc", "/var", "/boot", "/bin", "/sbin", "/lib", "/home", "/root" };

        if (forbidden.Contains(System.IO.Path.TrimEndingDirectorySeparator(mountPoint), StringComparer.Ordinal))
        {
            return $"Refusing to mount over {mountPoint}: the running system needs it.";
        }

        var root = System.IO.Path.GetFullPath(options.MountRoot);
        var resolved = System.IO.Path.GetFullPath(mountPoint);

        // Outside the mount root, the directory has to already exist — this will not create a
        // path anywhere it likes.
        if (!resolved.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Directory.Exists(resolved))
        {
            return $"'{resolved}' does not exist. Either create it first, or mount under {options.MountRoot}.";
        }

        return null;
    }

    /// <summary>
    /// Whether a device is mounted at boot — by anyone, not only by this module.
    /// </summary>
    /// <remarks>
    /// The whole file, not just the managed block. The question being answered is "does this come
    /// back after a reboot?", and the answer is yes whether the entry was written here or by the
    /// installer. Checking only the managed block labelled the machine's own root filesystem as
    /// temporary, which is both wrong and worrying.
    /// </remarks>
    public bool IsInFstab(BlockDevice device)
    {
        if (device.Uuid is not { Length: > 0 } uuid)
        {
            return false;
        }

        try
        {
            if (!File.Exists(options.FstabPath))
            {
                return false;
            }

            foreach (var line in File.ReadLines(options.FstabPath))
            {
                var trimmed = line.TrimStart();

                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                // Matches the UUID token exactly rather than by substring: fstab may reference the
                // device path or a label, and a UUID is a distinctive enough string that a
                // substring hit elsewhere would be a bug rather than a coincidence.
                if (trimmed.Split(' ', '\t').FirstOrDefault() is { } source
                    && source.Equals($"UUID={uuid}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {FstabPath}.", options.FstabPath);

            return false;
        }
    }

    /// <summary>
    /// Whether an fstab entry for this device is one this module wrote, and so may remove.
    /// </summary>
    /// <remarks>
    /// The admin's entries are not this module's to delete — an unmount should not quietly strip
    /// the root filesystem out of fstab.
    /// </remarks>
    public bool IsManagedInFstab(BlockDevice device) =>
        device.Uuid is { Length: > 0 } uuid
        && ReadManagedLines().Any(l => l.Contains($"UUID={uuid}", StringComparison.Ordinal));

    /// <summary>
    /// Adds a device to fstab so it comes back at boot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry uses <c>nofail</c>, deliberately. Without it, a disk that is missing or dead at
    /// boot drops the machine into an emergency shell — a headless home server that does not come
    /// back after a reboot because an external drive was unplugged is a genuinely bad failure. With
    /// it, the machine boots and the mount is simply absent.
    /// </para>
    /// <para>
    /// Entries live in a marked block, as with avahi's hosts file: fstab belongs to the admin, and
    /// only what is between the markers is this module's to rewrite.
    /// </para>
    /// </remarks>
    public async Task<MountResult> AddToFstabAsync(
        BlockDevice device,
        string mountPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Uuid is not { Length: > 0 } uuid)
        {
            // Without a UUID the only handle is the kernel name, which can move between boots.
            // An fstab entry that mounts whatever ends up at /dev/sdb is worse than none.
            return new MountResult
            {
                Succeeded = false,
                Message = $"{device.Name} has no filesystem UUID, so it cannot be mounted reliably at boot.",
            };
        }

        if (device.FileSystem is not { Length: > 0 } fileSystem)
        {
            return new MountResult { Succeeded = false, Message = $"{device.Name} has no filesystem." };
        }

        var entry = $"UUID={uuid}  {mountPoint}  {fileSystem}  defaults,nofail  0  2";
        var lines = ReadManagedLines().Where(l => !l.Contains($"UUID={uuid}", StringComparison.Ordinal)).ToList();
        lines.Add(entry);

        return await WriteManagedBlockAsync(lines, $"{device.Name} will be mounted at {mountPoint} at boot.", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes a device's entry from this module's fstab block.</summary>
    public async Task<MountResult> RemoveFromFstabAsync(
        BlockDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Uuid is not { Length: > 0 } uuid)
        {
            return new MountResult { Succeeded = false, Message = "No UUID, so there is no entry to remove." };
        }

        var lines = ReadManagedLines().Where(l => !l.Contains($"UUID={uuid}", StringComparison.Ordinal)).ToList();

        return await WriteManagedBlockAsync(lines, $"{device.Name} will no longer be mounted at boot.", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The entries currently inside this module's block.</summary>
    public IReadOnlyList<string> ReadManagedLines()
    {
        try
        {
            if (!File.Exists(options.FstabPath))
            {
                return [];
            }

            var lines = new List<string>();
            var inBlock = false;

            foreach (var line in File.ReadLines(options.FstabPath))
            {
                if (line.StartsWith(BeginMarker, StringComparison.Ordinal))
                {
                    inBlock = true;

                    continue;
                }

                if (line.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    break;
                }

                if (inBlock && line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))
                {
                    lines.Add(line.Trim());
                }
            }

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {FstabPath}.", options.FstabPath);

            return [];
        }
    }

    private async Task<MountResult> WriteManagedBlockAsync(
        IReadOnlyList<string> entries,
        string successMessage,
        CancellationToken cancellationToken)
    {
        string existing;

        try
        {
            existing = File.Exists(options.FstabPath)
                ? await File.ReadAllTextAsync(options.FstabPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MountResult { Succeeded = false, Message = $"Could not read fstab: {ex.Message}" };
        }

        var builder = new StringBuilder();
        builder.Append(StripManagedBlock(existing).TrimEnd('\n'));
        builder.Append('\n');

        if (entries.Count > 0)
        {
            builder.Append('\n').Append(BeginMarker).Append('\n');

            foreach (var entry in entries)
            {
                builder.Append(entry).Append('\n');
            }

            builder.Append(EndMarker).Append('\n');
        }

        try
        {
            // Atomic write, via the SDK: a half-written fstab is a machine that does not boot.
            await systemRunner.WriteFileAsync(options.FstabPath, builder.ToString(), cancellationToken)
                .ConfigureAwait(false);

            // systemd generates mount units from fstab at boot and caches them; without this the
            // change is on disk but invisible until reboot — which is the worst time to find out
            // it was wrong.
            await systemRunner.SystemctlAsync(SystemctlAction.DaemonReload, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Updated the managed block in {FstabPath} ({Count} entries).", options.FstabPath, entries.Count);

            return new MountResult { Succeeded = true, Message = successMessage };
        }
        catch (SystemOperationException ex)
        {
            return new MountResult { Succeeded = false, Message = $"Could not write fstab: {ex.Message}" };
        }
    }

    /// <summary>Removes this module's block, leaving everything the admin wrote untouched.</summary>
    private static string StripManagedBlock(string content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var kept = new List<string>();
        var inBlock = false;

        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                inBlock = true;

                continue;
            }

            if (inBlock)
            {
                if (line.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    inBlock = false;
                }

                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }
}
