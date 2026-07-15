using System.Collections.Immutable;
using System.Globalization;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.MergerFs;

/// <summary>
/// Creates and removes mergerfs pools, as systemd mount units.
/// </summary>
/// <remarks>
/// <para>
/// A unit rather than an fstab line, because the pool has to be ordered after the filesystems it
/// pools. fstab can express the dependency only through <c>x-systemd.requires=</c> on every
/// branch, which is the same unit written less legibly.
/// </para>
/// <para>
/// Creating a pool destroys nothing: it mounts existing filesystems at a new path, and removing it
/// leaves every branch and every file exactly as they were. The risk worth guarding is mounting
/// over a directory that already holds something, which hides it rather than deletes it, and is
/// confusing precisely because nothing is lost.
/// </para>
/// </remarks>
public sealed class PoolManager(ISystemRunner systemRunner, ILogger<PoolManager> logger)
{
    /// <summary>Where systemd keeps units an administrator installed.</summary>
    private const string UnitDirectory = "/etc/systemd/system";

    /// <summary>
    /// Marks the units this module wrote, so it never rewrites one someone else made.
    /// </summary>
    private const string ManagedMarker = "# Managed by EasyHomeServer.";

    /// <summary>Paths a pool may never be mounted on, because mounting over them breaks the system.</summary>
    private static readonly ImmutableArray<string> ForbiddenMountPoints =
    [
        "/", "/boot", "/boot/efi", "/dev", "/etc", "/home", "/proc", "/root", "/run", "/sys",
        "/tmp", "/usr", "/var",
    ];

    /// <summary>The outcome of a change.</summary>
    public sealed record PoolResult
    {
        /// <summary>Whether it worked.</summary>
        public required bool Succeeded { get; init; }

        /// <summary>What happened, in words.</summary>
        public required string Message { get; init; }
    }

    /// <summary>
    /// Everything wrong with a spec, as sentences.
    /// </summary>
    /// <remarks>
    /// All of them at once rather than the first: these are answers to a form, and reporting one
    /// problem per attempt makes filling it in a guessing game.
    /// </remarks>
    public ImmutableArray<string> Validate(PoolSpec spec)
    {
        var problems = ImmutableArray.CreateBuilder<string>();

        var mountPoint = NormalisePath(spec.MountPoint);

        if (mountPoint.Length == 0 || !mountPoint.StartsWith('/'))
        {
            problems.Add("The mount point must be an absolute path, such as /data/storage.");
        }
        else if (ForbiddenMountPoints.Contains(mountPoint, StringComparer.Ordinal))
        {
            problems.Add($"{mountPoint} belongs to the system; mounting a pool there would hide it.");
        }

        if (spec.Branches.Length < 2)
        {
            problems.Add("A pool needs at least two branches. With one, it is the filesystem it already is.");
        }

        if (spec.Branches.Distinct(StringComparer.Ordinal).Count() != spec.Branches.Length)
        {
            problems.Add("The same branch is listed more than once.");
        }

        foreach (var branch in spec.Branches)
        {
            var path = NormalisePath(branch);

            if (!Directory.Exists(path))
            {
                problems.Add($"{branch} does not exist.");

                continue;
            }

            // A pool inside a branch, or a branch inside the pool, is a loop.
            if (IsWithin(mountPoint, path) || IsWithin(path, mountPoint))
            {
                problems.Add($"{branch} and {mountPoint} contain one another; a pool cannot include its own mount point.");
            }
        }

        // The trap: minfreespace filters branches before the create policy runs, so a threshold no
        // branch can meet produces a pool that mounts, reports its space, and fails every write
        // with ENOSPC. Cheaper to refuse here than to explain later.
        if (SmallestBranchBytes(spec.Branches) is { } smallest && spec.MinFreeSpaceBytes >= smallest)
        {
            problems.Add(
                $"Min free space ({Format.Bytes(spec.MinFreeSpaceBytes)}) is larger than the smallest "
                + $"branch ({Format.Bytes(smallest)}), so no branch could ever take a new file and the "
                + "pool would fail every write with ENOSPC while still reporting free space.");
        }

        if (mountPoint.Length > 0 && HasContent(mountPoint))
        {
            problems.Add(
                $"{mountPoint} is not empty. Mounting a pool over it would hide what is there — nothing "
                + "is deleted, but it becomes unreachable until the pool is unmounted.");
        }

        return problems.ToImmutable();
    }

    /// <summary>Creates the pool and mounts it, now and at boot.</summary>
    public async Task<PoolResult> CreateAsync(PoolSpec spec, CancellationToken cancellationToken = default)
    {
        var problems = Validate(spec);

        if (problems.Length > 0)
        {
            return new PoolResult { Succeeded = false, Message = problems[0] };
        }

        var mountPoint = NormalisePath(spec.MountPoint);

        try
        {
            var unitName = await EscapePathAsync(mountPoint, cancellationToken).ConfigureAwait(false);

            if (unitName is null)
            {
                return new PoolResult
                {
                    Succeeded = false,
                    Message = "Could not work out the systemd unit name for that mount point.",
                };
            }

            var unitPath = Path.Combine(UnitDirectory, unitName);

            if (File.Exists(unitPath) && !await IsManagedUnitAsync(unitPath, cancellationToken).ConfigureAwait(false))
            {
                return new PoolResult
                {
                    Succeeded = false,
                    Message = $"{unitPath} already exists and was not written here, so it will not be replaced.",
                };
            }

            var branchUnits = await EscapeBranchesAsync(spec.Branches, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(mountPoint);

            await systemRunner
                .WriteFileAsync(unitPath, BuildUnit(spec, mountPoint, branchUnits), cancellationToken)
                .ConfigureAwait(false);

            await systemRunner.SystemctlAsync(SystemctlAction.DaemonReload, null, cancellationToken)
                .ConfigureAwait(false);

            // Enable, then start: enabled but unmounted is recoverable by hand, mounted but not
            // enabled quietly disappears at the next reboot.
            var enable = await systemRunner.SystemctlAsync(SystemctlAction.Enable, unitName, cancellationToken)
                .ConfigureAwait(false);

            if (!enable.Succeeded)
            {
                return new PoolResult
                {
                    Succeeded = false,
                    Message = $"Could not enable {unitName}: {enable.StandardError.Trim()}",
                };
            }

            var start = await systemRunner.SystemctlAsync(SystemctlAction.Start, unitName, cancellationToken)
                .ConfigureAwait(false);

            if (!start.Succeeded)
            {
                return new PoolResult
                {
                    Succeeded = false,
                    Message = $"Wrote {unitPath}, but mounting failed: {start.StandardError.Trim()}",
                };
            }

            logger.LogInformation("Created pool {MountPoint} from {BranchCount} branches ({Unit}).",
                mountPoint, spec.Branches.Length, unitName);

            return new PoolResult
            {
                Succeeded = true,
                Message = $"Pool mounted at {mountPoint} from {spec.Branches.Length} branches.",
            };
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not create the pool at {MountPoint}.", mountPoint);

            return new PoolResult { Succeeded = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Unmounts a pool and stops it coming back at boot.
    /// </summary>
    /// <remarks>
    /// Keeps the unit as a <c>.bak</c> rather than deleting it. systemd ignores any suffix it does
    /// not know, so the file stops being a unit the moment it is renamed, and a pool someone tuned
    /// by hand can be put back by renaming it again. Deleting it would throw away the only record
    /// of how the pool was built to save a file of a few hundred bytes.
    /// </remarks>
    public async Task<PoolResult> RemoveAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        var path = NormalisePath(mountPoint);

        try
        {
            var unitName = await EscapePathAsync(path, cancellationToken).ConfigureAwait(false);

            if (unitName is null)
            {
                return new PoolResult
                {
                    Succeeded = false,
                    Message = "Could not work out the systemd unit name for that mount point.",
                };
            }

            var stop = await systemRunner.SystemctlAsync(SystemctlAction.Stop, unitName, cancellationToken)
                .ConfigureAwait(false);

            if (!stop.Succeeded)
            {
                // Almost always something holding a file open under the pool.
                return new PoolResult
                {
                    Succeeded = false,
                    Message = $"Could not unmount {path}: {stop.StandardError.Trim()} "
                              + "Something is probably still using it — a container, a share, or a shell.",
                };
            }

            await systemRunner.SystemctlAsync(SystemctlAction.Disable, unitName, cancellationToken)
                .ConfigureAwait(false);

            var unitPath = Path.Combine(UnitDirectory, unitName);
            var backupPath = unitPath + ".bak";

            if (File.Exists(unitPath))
            {
                var moved = await systemRunner
                    .RunAsync("mv", ["-f", unitPath, backupPath], cancellationToken)
                    .ConfigureAwait(false);

                if (!moved.Succeeded)
                {
                    return new PoolResult
                    {
                        Succeeded = false,
                        Message = $"Unmounted {path}, but could not move {unitPath} aside: "
                                  + moved.StandardError.Trim(),
                    };
                }
            }

            await systemRunner.SystemctlAsync(SystemctlAction.DaemonReload, null, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Removed pool {MountPoint}; unit kept at {BackupPath}.", path, backupPath);

            return new PoolResult
            {
                Succeeded = true,
                Message = $"Pool {path} removed. Every file is still on the branch that held it. "
                          + $"The unit is kept at {backupPath}.",
            };
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not remove the pool at {MountPoint}.", path);

            return new PoolResult { Succeeded = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// The unit file's text.
    /// </summary>
    /// <remarks>
    /// <c>Requires=</c> on the branches, not just <c>After=</c>: a pool that mounts while a branch
    /// is missing looks healthy and silently serves an incomplete set of files, and new writes land
    /// on the remaining disks. Refusing to mount is the louder and safer failure.
    /// </remarks>
    private static string BuildUnit(PoolSpec spec, string mountPoint, ImmutableArray<string> branchUnits)
    {
        var dependencies = branchUnits.Length > 0 ? string.Join(' ', branchUnits) : null;

        var options = string.Join(',',
        [
            "defaults",
            "noatime",
            // Without noforget, mergerfs can exhaust kernel memory tracking inodes on a big pool.
            "noforget",
            "inodecalc=path-hash",
            "allow_other",
            $"category.create={spec.CreatePolicy}",
            $"moveonenospc={(spec.MoveOnEnoSpc ? "true" : "false")}",
            $"minfreespace={FormatSize(spec.MinFreeSpaceBytes)}",
            // fsname keeps df readable; without it the whole branch list appears as the source.
            "fsname=mergerfs",
        ]);

        var unit = new System.Text.StringBuilder();

        unit.AppendLine(ManagedMarker);
        unit.AppendLine("# Removing this file by hand leaves the branches and their files untouched.");
        unit.AppendLine();
        unit.AppendLine("[Unit]");
        unit.AppendLine("Description=MergerFS Storage Pool");

        if (dependencies is not null)
        {
            unit.AppendLine(CultureInfo.InvariantCulture, $"After={dependencies}");
            unit.AppendLine(CultureInfo.InvariantCulture, $"Requires={dependencies}");
        }

        unit.AppendLine();
        unit.AppendLine("[Mount]");
        unit.AppendLine(CultureInfo.InvariantCulture, $"What={string.Join(':', spec.Branches.Select(NormalisePath))}");
        unit.AppendLine(CultureInfo.InvariantCulture, $"Where={mountPoint}");
        unit.AppendLine("Type=fuse.mergerfs");
        unit.AppendLine(CultureInfo.InvariantCulture, $"Options={options}");
        unit.AppendLine();
        unit.AppendLine("[Install]");
        unit.AppendLine("WantedBy=multi-user.target");

        return unit.ToString();
    }

    /// <summary>
    /// Asks systemd what it calls the unit for a path.
    /// </summary>
    /// <remarks>
    /// systemd-escape rather than string replacement, because a mount unit whose filename does not
    /// match its <c>Where=</c> is rejected, and the rules are not obvious: <c>/mnt/my-data</c>
    /// becomes <c>mnt-my\x2ddata.mount</c>, escaping the dash that a naive replacement would keep.
    /// </remarks>
    private async Task<string?> EscapePathAsync(string path, CancellationToken cancellationToken)
    {
        var result = await systemRunner
            .RunAsync("systemd-escape", ["-p", "--suffix=mount", path], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            logger.LogWarning("systemd-escape failed for {Path}: {Error}", path, result.StandardError.Trim());

            return null;
        }

        return result.StandardOutput.Trim() is { Length: > 0 } name ? name : null;
    }

    private async Task<ImmutableArray<string>> EscapeBranchesAsync(
        ImmutableArray<string> branches,
        CancellationToken cancellationToken)
    {
        var units = ImmutableArray.CreateBuilder<string>();

        foreach (var branch in branches)
        {
            if (await EscapePathAsync(NormalisePath(branch), cancellationToken).ConfigureAwait(false) is { } unit)
            {
                units.Add(unit);
            }
        }

        return units.ToImmutable();
    }

    /// <summary>Whether a unit file carries this module's marker.</summary>
    private async Task<bool> IsManagedUnitAsync(string unitPath, CancellationToken cancellationToken)
    {
        try
        {
            var content = await systemRunner.ReadFileAsync(unitPath, cancellationToken).ConfigureAwait(false);

            return content.Contains(ManagedMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// mergerfs's own spelling of a size: an integer with a unit suffix.
    /// </summary>
    /// <remarks>
    /// Exact multiples only. minfreespace=21474836480 is legal but unreadable in a unit file a
    /// person may later edit, and a rounded "20G" that did not mean 20G would be worse than both.
    /// </remarks>
    private static string FormatSize(long bytes)
    {
        const long Kibibyte = 1024;
        const long Mebibyte = Kibibyte * 1024;
        const long Gibibyte = Mebibyte * 1024;

        return bytes switch
        {
            _ when bytes >= Gibibyte && bytes % Gibibyte == 0 => $"{bytes / Gibibyte}G",
            _ when bytes >= Mebibyte && bytes % Mebibyte == 0 => $"{bytes / Mebibyte}M",
            _ when bytes >= Kibibyte && bytes % Kibibyte == 0 => $"{bytes / Kibibyte}K",
            _ => bytes.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The size of the smallest branch, or null when none could be read.</summary>
    private static long? SmallestBranchBytes(ImmutableArray<string> branches)
    {
        long? smallest = null;

        foreach (var branch in branches)
        {
            try
            {
                if (!Directory.Exists(branch))
                {
                    continue;
                }

                var size = new DriveInfo(branch).TotalSize;

                smallest = smallest is null ? size : Math.Min(smallest.Value, size);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable branches are reported by their own check.
            }
        }

        return smallest;
    }

    /// <summary>Whether a directory exists and has anything in it.</summary>
    private static bool HasContent(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="child"/> is inside <paramref name="parent"/>.</summary>
    private static bool IsWithin(string child, string parent) =>
        parent.Length > 0
        && child.Length > 0
        && (string.Equals(child, parent, StringComparison.Ordinal)
            || child.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal));

    private static string NormalisePath(string path) =>
        Path.TrimEndingDirectorySeparator(path.Trim());
}
