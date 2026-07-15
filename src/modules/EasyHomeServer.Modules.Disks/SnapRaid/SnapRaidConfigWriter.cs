using System.Collections.Immutable;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.SnapRaid;

/// <summary>An array to create.</summary>
public sealed record ArraySpec
{
    /// <summary>
    /// The parity file(s). One per disk that may fail.
    /// </summary>
    /// <remarks>
    /// Each must live on a disk that holds no data, and be at least as large as the largest data
    /// disk, since parity covers a whole disk's worth of blocks.
    /// </remarks>
    public required ImmutableArray<string> ParityFiles { get; init; }

    /// <summary>The data disks to protect.</summary>
    public required ImmutableArray<SnapRaidDataDisk> DataDisks { get; init; }
}

/// <summary>
/// Writes <c>snapraid.conf</c>.
/// </summary>
/// <remarks>
/// Only writes a file that is absent or that this module wrote. snapraid.conf is hand-edited on
/// most machines that have one, and it is the index of what parity means: replacing someone's
/// exclude rules would silently change what is protected, and they would find out at a restore.
/// </remarks>
public sealed class SnapRaidConfigWriter(
    ISystemRunner systemRunner,
    SnapRaidCli cli,
    DisksOptions options,
    ILogger<SnapRaidConfigWriter> logger)
{
    private const string ManagedMarker = "# Managed by EasyHomeServer.";

    /// <summary>
    /// Excludes every array wants: junk that changes constantly, costs a sync, and is worth nothing
    /// restored. The macOS ones matter here because this machine serves Time Machine and SMB.
    /// </summary>
    private static readonly ImmutableArray<string> DefaultExcludes =
    [
        "/lost+found/",
        "*.unrecoverable",
        ".Thumbs.db",
        ".DS_Store",
        "._*",
        ".AppleDouble",
        ".Spotlight-V100",
        ".fseventsd",
        ".Trashes",
        "/tmp/",
    ];

    /// <summary>The outcome of a change.</summary>
    public sealed record WriteResult
    {
        /// <summary>Whether it worked.</summary>
        public required bool Succeeded { get; init; }

        /// <summary>What happened, in words.</summary>
        public required string Message { get; init; }
    }

    /// <summary>A file SnapRAID made, and what it costs to keep.</summary>
    public sealed record ArrayArtifact
    {
        /// <summary>Where it is.</summary>
        public required string Path { get; init; }

        /// <summary>Its size, or null when it is not there.</summary>
        public long? SizeBytes { get; init; }

        /// <summary>Whether SnapRAID's parity lives here, as opposed to its index.</summary>
        public required bool IsParity { get; init; }
    }

    /// <summary>
    /// The files an array leaves on disk: parity, and the content files indexing what it protects.
    /// </summary>
    /// <remarks>
    /// Listed with their sizes because parity is the largest single file on the machine — a whole
    /// disk's worth — and someone deciding whether to delete it deserves to know that before
    /// rather than after.
    /// </remarks>
    public ImmutableArray<ArrayArtifact> ListArtifacts(SnapRaidConfig config) =>
    [
        .. config.ParityFiles.Select(path => new ArrayArtifact
        {
            Path = path,
            SizeBytes = FileSize(path),
            IsParity = true,
        }),
        .. config.ContentFiles.Select(path => new ArrayArtifact
        {
            Path = path,
            SizeBytes = FileSize(path),
            IsParity = false,
        }),
    ];

    /// <summary>
    /// Removes the array: moves snapraid.conf aside, and optionally deletes what it made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deletes no data. Parity and content files are SnapRAID's own; every protected file lives on
    /// its data disk untouched, and each disk stays independently readable — which is the property
    /// SnapRAID is chosen for in the first place.
    /// </para>
    /// <para>
    /// What is actually lost is the ability to rebuild a failed disk. Rebuilding the array later
    /// means a full sync, which reads every file.
    /// </para>
    /// </remarks>
    public async Task<WriteResult> RemoveAsync(
        SnapRaidConfig config,
        bool deleteArtifacts,
        CancellationToken cancellationToken = default)
    {
        // Deleting the parity a running sync is writing would leave it half-made and the config
        // gone, with nothing to say why.
        if (await cli.IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return new WriteResult
            {
                Succeeded = false,
                Message = "snapraid is running — a sync or scrub is in progress, and this machine may "
                          + "run one from cron. Let it finish, then try again.",
            };
        }

        try
        {
            var backupPath = options.SnapRaidConfigPath + ".bak";

            if (File.Exists(options.SnapRaidConfigPath))
            {
                // Kept, not deleted, for the same reason as the pool's unit: it is the record of
                // how the array was laid out, and it is a few kilobytes.
                var moved = await systemRunner
                    .RunAsync("mv", ["-f", options.SnapRaidConfigPath, backupPath], cancellationToken)
                    .ConfigureAwait(false);

                if (!moved.Succeeded)
                {
                    return new WriteResult
                    {
                        Succeeded = false,
                        Message = $"Could not move {options.SnapRaidConfigPath} aside: {moved.StandardError.Trim()}",
                    };
                }
            }

            var freed = 0L;
            var failures = new List<string>();

            if (deleteArtifacts)
            {
                foreach (var artifact in ListArtifacts(config).Where(a => a.SizeBytes is not null))
                {
                    var removed = await systemRunner
                        .RunAsync("rm", ["-f", artifact.Path], cancellationToken)
                        .ConfigureAwait(false);

                    if (removed.Succeeded)
                    {
                        freed += artifact.SizeBytes ?? 0;
                    }
                    else
                    {
                        failures.Add(artifact.Path);
                    }
                }
            }

            logger.LogInformation("Removed the array; config kept at {BackupPath}, freed {Freed} bytes.",
                backupPath, freed);

            var message = deleteArtifacts
                ? $"Array removed and {Format.Bytes(freed)} freed. Every protected file is untouched on "
                  + $"its data disk. The configuration is kept at {backupPath}."
                : $"Array removed. Parity and content files were left in place. The configuration is "
                  + $"kept at {backupPath}.";

            if (failures.Count > 0)
            {
                message += $" Could not delete: {string.Join(", ", failures)}.";
            }

            return new WriteResult { Succeeded = true, Message = message };
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not remove the array.");

            return new WriteResult { Succeeded = false, Message = ex.Message };
        }
    }

    private static long? FileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a config already exists, whoever wrote it.</summary>
    public bool ConfigExists => File.Exists(options.SnapRaidConfigPath);

    /// <summary>Everything wrong with a spec, as sentences.</summary>
    public ImmutableArray<string> Validate(ArraySpec spec)
    {
        var problems = ImmutableArray.CreateBuilder<string>();

        if (spec.ParityFiles.Length == 0)
        {
            problems.Add("An array needs at least one parity file, on a disk that holds no data.");
        }

        if (spec.DataDisks.Length < 2)
        {
            problems.Add("An array needs at least two data disks for parity to be worth computing.");
        }

        foreach (var parity in spec.ParityFiles)
        {
            var directory = Path.GetDirectoryName(parity);

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                problems.Add($"The directory for parity file {parity} does not exist.");

                continue;
            }

            // Parity on a data disk protects nothing: the disk that fails takes its own parity.
            if (spec.DataDisks.Any(disk => IsWithin(parity, disk.Path)))
            {
                problems.Add($"Parity file {parity} is on a data disk, so that disk failing would take "
                             + "its own parity with it. Parity must live on a disk of its own.");
            }
        }

        foreach (var disk in spec.DataDisks)
        {
            if (!Directory.Exists(disk.Path))
            {
                problems.Add($"{disk.Path} does not exist.");
            }
        }

        if (spec.DataDisks.Select(disk => disk.Name).Distinct(StringComparer.Ordinal).Count()
            != spec.DataDisks.Length)
        {
            problems.Add("Two data disks share a name.");
        }

        // Parity must cover the largest data disk, or it cannot rebuild it.
        if (LargestBytes(spec.DataDisks.Select(disk => disk.Path)) is { } largestData
            && SmallestBytes(spec.ParityFiles.Select(Path.GetDirectoryName).OfType<string>()) is { } smallestParity
            && smallestParity < largestData)
        {
            problems.Add(
                $"The parity disk ({Format.Bytes(smallestParity)}) is smaller than the largest data disk "
                + $"({Format.Bytes(largestData)}). Parity has to hold a full disk's worth of blocks, so it "
                + "cannot protect a disk larger than itself.");
        }

        return problems.ToImmutable();
    }

    /// <summary>Writes a new snapraid.conf.</summary>
    public async Task<WriteResult> CreateAsync(ArraySpec spec, CancellationToken cancellationToken = default)
    {
        var problems = Validate(spec);

        if (problems.Length > 0)
        {
            return new WriteResult { Succeeded = false, Message = problems[0] };
        }

        if (ConfigExists && !await IsManagedAsync(cancellationToken).ConfigureAwait(false))
        {
            return new WriteResult
            {
                Succeeded = false,
                Message = $"{options.SnapRaidConfigPath} already exists and was not written here. It is the "
                          + "index of what parity protects, so it will not be replaced.",
            };
        }

        try
        {
            await systemRunner
                .WriteFileAsync(options.SnapRaidConfigPath, Build(spec), cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Wrote {Path}: {ParityCount} parity, {DataCount} data disks.",
                options.SnapRaidConfigPath, spec.ParityFiles.Length, spec.DataDisks.Length);

            return new WriteResult
            {
                Succeeded = true,
                // Says the next step, because a written config protects nothing until a sync runs.
                Message = $"Wrote {options.SnapRaidConfigPath}. Nothing is protected until the first "
                          + "sync runs, which reads every file and can take hours.",
            };
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not write {Path}.", options.SnapRaidConfigPath);

            return new WriteResult { Succeeded = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// The config file's text.
    /// </summary>
    /// <remarks>
    /// A content file goes on every data disk as well as on the system disk. SnapRAID needs one to
    /// know what it protects, and copies cost nothing next to losing the array's index with the
    /// disk it sat on.
    /// </remarks>
    private string Build(ArraySpec spec)
    {
        var config = new System.Text.StringBuilder();

        config.AppendLine(ManagedMarker);
        config.AppendLine("# The array SnapRAID protects. Parity is computed across the data disks below;");
        config.AppendLine("# each disk keeps its own filesystem and stays readable on its own.");
        config.AppendLine();

        foreach (var (parity, index) in spec.ParityFiles.Select((path, index) => (path, index)))
        {
            // The first is "parity", the rest are "2-parity", "3-parity"...
            config.AppendLine(index == 0 ? $"parity {parity}" : $"{index + 1}-parity {parity}");
        }

        config.AppendLine();
        config.AppendLine("# SnapRAID's index of what it protects, kept in several places on purpose.");
        config.AppendLine($"content {options.SnapRaidContentRoot}/content");

        foreach (var disk in spec.DataDisks)
        {
            config.AppendLine($"content {disk.Path}/.snapraid.content");
        }

        config.AppendLine();

        foreach (var disk in spec.DataDisks)
        {
            config.AppendLine($"data {disk.Name} {disk.Path}/");
        }

        config.AppendLine();
        config.AppendLine("# Not worth protecting: rebuilt on demand, or changes constantly.");

        foreach (var exclude in DefaultExcludes)
        {
            config.AppendLine($"exclude {exclude}");
        }

        return config.ToString();
    }

    private async Task<bool> IsManagedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var content = await systemRunner.ReadFileAsync(options.SnapRaidConfigPath, cancellationToken)
                .ConfigureAwait(false);

            return content.Contains(ManagedMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SystemOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long? LargestBytes(IEnumerable<string> paths) =>
        Sizes(paths).Select(size => (long?)size).DefaultIfEmpty(null).Max();

    private static long? SmallestBytes(IEnumerable<string> paths) =>
        Sizes(paths).Select(size => (long?)size).DefaultIfEmpty(null).Min();

    private static IEnumerable<long> Sizes(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            long size;

            try
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                size = new DriveInfo(path).TotalSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            yield return size;
        }
    }

    private static bool IsWithin(string child, string parent) =>
        parent.Length > 0
        && child.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);
}
