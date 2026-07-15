using System.Collections.Immutable;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>
/// Prepares a blank disk for use: partition table, one partition, filesystem.
/// </summary>
/// <remarks>
/// <para>
/// The one flow worth automating. Plugging a new disk into a home server and wanting all of it as
/// one filesystem is overwhelmingly the common case, and doing it by hand means <c>sgdisk</c> or
/// <c>parted</c>, then <c>mkfs</c>, then finding the new UUID, then editing fstab — four chances to
/// mistype a device name that destroys the wrong disk.
/// </para>
/// <para>
/// Anything more elaborate — several partitions, resizing, LVM, RAID — is deliberately absent.
/// Those need choices this cannot make on someone's behalf, and a wrong guess is unrecoverable.
/// </para>
/// <para>
/// The guards here are the point of the class. Everything else is three shell commands.
/// </para>
/// </remarks>
public sealed class DiskFormatter(ISystemRunner systemRunner, ILogger<DiskFormatter> logger)
{
    /// <summary>Filesystems offered. Each has a reason to be here.</summary>
    public static ImmutableArray<FileSystemChoice> FileSystems { get; } =
    [
        new()
        {
            Id = "ext4",
            Name = "ext4",
            Description = "The default. Mature, fast, and every recovery tool understands it.",
            MakeCommand = "mkfs.ext4",
        },
        new()
        {
            Id = "xfs",
            Name = "XFS",
            Description = "Good with large files and parallel writes. Cannot be shrunk.",
            MakeCommand = "mkfs.xfs",
        },
        new()
        {
            Id = "btrfs",
            Name = "Btrfs",
            Description = "Snapshots and checksums, at the cost of more moving parts.",
            MakeCommand = "mkfs.btrfs",
        },
    ];

    /// <summary>A filesystem the disk can be formatted with.</summary>
    public sealed record FileSystemChoice
    {
        /// <summary>Value used on the wire and in fstab.</summary>
        public required string Id { get; init; }

        /// <summary>Name as shown.</summary>
        public required string Name { get; init; }

        /// <summary>One line on why you would pick it.</summary>
        public required string Description { get; init; }

        /// <summary>The mkfs binary, which may not be installed.</summary>
        public required string MakeCommand { get; init; }
    }

    /// <summary>
    /// Says why a disk must not be formatted, or null when it may be.
    /// </summary>
    /// <remarks>
    /// Public so the UI can disable the button and say why, rather than offering an action that
    /// will be refused. Checked again at the point of use — a disk can be mounted between the page
    /// rendering and the button being pressed.
    /// </remarks>
    public static string? DescribeRefusal(BlockDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Kind != DeviceKind.Disk)
        {
            return "Only a whole disk can be prepared this way.";
        }

        if (device.IsSystemDisk)
        {
            return "This disk holds the running system.";
        }

        if (device.IsInUse)
        {
            var mounted = Describe(device);

            return $"It is in use ({mounted}). Unmount it first.";
        }

        if (device.IsReadOnly)
        {
            return "The kernel has this device read-only.";
        }

        if (device.Kind == DeviceKind.Rom)
        {
            return "This is an optical drive.";
        }

        return null;
    }

    private static string Describe(BlockDevice device)
    {
        var mounts = Collect(device).ToList();

        return mounts.Count > 0 ? string.Join(", ", mounts) : "mounted";

        static IEnumerable<string> Collect(BlockDevice device)
        {
            foreach (var mount in device.MountPoints)
            {
                yield return mount;
            }

            foreach (var mount in device.Children.SelectMany(Collect))
            {
                yield return mount;
            }
        }
    }

    /// <summary>
    /// The exact commands a plan would run, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built once and used for both the preview and the run, so the list shown to someone before
    /// they type a device name is the list that executes. A preview assembled separately is a
    /// second implementation of the same logic, and the day the two drift is the day someone
    /// approves one thing and gets another.
    /// </para>
    /// <para>
    /// mkfs is included per partition even though the kernel has not been told about those
    /// partitions yet at preview time — the paths are computed, not probed.
    /// </para>
    /// </remarks>
    public static ImmutableArray<PlannedCommand> PlanCommands(BlockDevice device, PartitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(plan);

        var commands = ImmutableArray.CreateBuilder<PlannedCommand>();

        // Old signatures left in place confuse blkid, which then reports a filesystem that is not
        // there any more.
        commands.Add(new PlannedCommand
        {
            Description = "clearing old signatures",
            File = "wipefs",
            Arguments = ["--all", "--force", device.Path],
        });

        // GPT unconditionally: MBR cannot address past 2 TB, and a disk in a home server today is
        // usually larger than that.
        commands.Add(new PlannedCommand
        {
            Description = "writing a GPT partition table",
            File = "sgdisk",
            Arguments = ["--zap-all", device.Path],
        });

        foreach (var (partition, index) in plan.Partitions.Select((p, i) => (p, i)))
        {
            var number = index + 1;

            commands.Add(new PlannedCommand
            {
                Description = $"creating partition {number}",
                File = "sgdisk",
                Arguments =
                [
                    // Start 0: the first aligned free sector, so partitions pack in order.
                    $"--new={number}:0:{PartitionPlan.SizeArgument(partition)}",
                    $"--typecode={number}:8300",
                    device.Path,
                ],
            });
        }

        foreach (var (partition, index) in plan.Partitions.Select((p, i) => (p, i)))
        {
            var number = index + 1;

            if (FileSystems.FirstOrDefault(f => f.Id == partition.FileSystemId) is not { } fileSystem)
            {
                continue;
            }

            commands.Add(new PlannedCommand
            {
                Description = $"making {fileSystem.Name} on partition {number}",
                File = fileSystem.MakeCommand,
                Arguments = [.. MakeArguments(fileSystem, partition.Label, PartitionPath(device.Path, number))],
            });
        }

        return commands.ToImmutable();
    }

    /// <summary>Applies a whole partition layout, destroying everything on the disk.</summary>
    /// <remarks>
    /// The table is rewritten from empty rather than edited. Editing in place means moving data and
    /// shrinking filesystems, which is a different and much more dangerous program.
    /// </remarks>
    public async Task<FormatResult> PartitionAsync(
        BlockDevice device,
        PartitionPlan plan,
        string confirmedDeviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(plan);

        if (DescribeRefusal(device) is { } refusal)
        {
            return new FormatResult { Succeeded = false, Message = $"Refusing to partition {device.Name}: {refusal}" };
        }

        if (!string.Equals(confirmedDeviceName, device.Name, StringComparison.Ordinal))
        {
            return new FormatResult { Succeeded = false, Message = "The typed device name does not match." };
        }

        if (plan.Validate(device.SizeBytes) is { Length: > 0 } problems)
        {
            return new FormatResult { Succeeded = false, Message = problems[0] };
        }

        // Each partition must name a filesystem this knows. Checked per partition rather than by
        // counting a de-duplicated list: three ext4 partitions collapse to one entry, and comparing
        // that count to the partition count calls every uniform plan invalid.
        if (plan.Partitions.FirstOrDefault(p => FileSystems.All(f => f.Id != p.FileSystemId)) is { } unknown)
        {
            return new FormatResult
            {
                Succeeded = false,
                Message = $"'{unknown.FileSystemId}' is not a filesystem this can make.",
            };
        }

        var fileSystems = plan.Partitions
            .Select(p => FileSystems.First(f => f.Id == p.FileSystemId))
            .Distinct()
            .ToList();

        // Every tool for every filesystem in the plan, before touching anything — the same reason
        // as PrepareAsync, multiplied: a plan mixing ext4 and XFS can be half-formattable, and
        // finding that out after the table is written leaves a disk of unusable partitions.
        if (await FindMissingToolsAsync(fileSystems, cancellationToken).ConfigureAwait(false) is { Count: > 0 } missing)
        {
            var packages = string.Join(" ", missing.Select(PackageForTool).Distinct());

            return new FormatResult
            {
                Succeeded = false,
                Message = $"Nothing has been changed. {string.Join(", ", missing)} "
                          + $"{(missing.Count == 1 ? "is" : "are")} not installed. Install with: apt install {packages}",
            };
        }

        logger.LogWarning(
            "Partitioning {Device} ({Size} bytes) into {Count} partitions. Everything on it is being destroyed.",
            device.Path,
            device.SizeBytes,
            plan.Partitions.Length);

        var commands = PlanCommands(device, plan);

        // The partition table has to exist before mkfs can be pointed at it, and the kernel learns
        // about it asynchronously.
        var settled = false;

        foreach (var command in commands)
        {
            if (!settled && command.IsMakeFileSystem)
            {
                await SettleAsync(cancellationToken).ConfigureAwait(false);
                settled = true;
            }

            var failure = await RunAsync(command.File, command.Arguments, command.Description, device, cancellationToken)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                return failure;
            }
        }

        await SettleAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Partitioned {Device} into {Count} partitions.", device.Path, plan.Partitions.Length);

        return new FormatResult
        {
            Succeeded = true,
            Message = $"{device.Name} now has {plan.Partitions.Length} "
                      + $"partition{(plan.Partitions.Length == 1 ? "" : "s")}. Mount them to use them.",
            PartitionPath = PartitionPath(device.Path, 1),
        };
    }

    /// <summary>The arguments mkfs needs for a filesystem, label and target.</summary>
    private static IEnumerable<string> MakeArguments(FileSystemChoice fileSystem, string? label, string target)
    {
        if (label is { Length: > 0 })
        {
            yield return "-L";
            yield return label;
        }

        // Non-interactive: mkfs asks for confirmation on a device that looks used, and there is no
        // one to answer.
        if (fileSystem.Id is "ext4")
        {
            yield return "-F";
        }
        else if (fileSystem.Id is "btrfs" or "xfs")
        {
            yield return "-f";
        }

        yield return target;
    }

    /// <summary>
    /// Wipes a disk, gives it a GPT with one full-size partition, and makes a filesystem on it.
    /// </summary>
    /// <remarks>
    /// <paramref name="confirmedDeviceName"/> must equal the device's own name. The caller has
    /// already asked; this checks again, because every argument here is a device path and the cost
    /// of the wrong one is somebody's data.
    /// </remarks>
    public async Task<FormatResult> PrepareAsync(
        BlockDevice device,
        string fileSystemId,
        string? label,
        string confirmedDeviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (DescribeRefusal(device) is { } refusal)
        {
            return new FormatResult { Succeeded = false, Message = $"Refusing to prepare {device.Name}: {refusal}" };
        }

        if (!string.Equals(confirmedDeviceName, device.Name, StringComparison.Ordinal))
        {
            return new FormatResult { Succeeded = false, Message = "The typed device name does not match." };
        }

        if (FileSystems.FirstOrDefault(f => f.Id == fileSystemId) is not { } fileSystem)
        {
            return new FormatResult { Succeeded = false, Message = $"Unknown filesystem '{fileSystemId}'." };
        }

        logger.LogWarning(
            "Preparing {Device} ({Size} bytes) with {FileSystem}. Everything on it is being destroyed.",
            device.Path,
            device.SizeBytes,
            fileSystem.Id);

        // Every tool, before touching anything. The first step destroys the disk's signatures, so
        // discovering a missing sgdisk after that leaves a disk that is wiped, unpartitioned and
        // unusable — worse than never having started. Checked in one pass so the operator is told
        // once, up front, rather than a step at a time.
        if (await FindMissingToolsAsync(fileSystem, cancellationToken).ConfigureAwait(false) is { Count: > 0 } missing)
        {
            var packages = string.Join(" ", missing.Select(PackageForTool).Distinct());

            return new FormatResult
            {
                Succeeded = false,
                Message = $"Nothing has been changed. {string.Join(", ", missing)} "
                          + $"{(missing.Count == 1 ? "is" : "are")} not installed. Install with: apt install {packages}",
            };
        }

        var steps = new (string Description, string File, string[] Arguments)[]
        {
            // Old signatures left in place confuse blkid, which then reports a filesystem that is
            // not there any more.
            ("clearing old signatures", "wipefs", ["--all", "--force", device.Path]),

            // GPT unconditionally: MBR cannot address past 2 TB, and a disk in a home server today
            // is usually larger than that.
            ("writing a GPT partition table", "sgdisk", ["--zap-all", device.Path]),
            ("creating a partition", "sgdisk", ["--new=1:0:0", "--typecode=1:8300", device.Path]),
        };

        foreach (var (description, file, arguments) in steps)
        {
            var result = await RunAsync(file, arguments, description, device, cancellationToken).ConfigureAwait(false);

            if (result is not null)
            {
                return result;
            }
        }

        // The kernel learns about the new partition asynchronously; mkfs on a path that does not
        // exist yet fails intermittently, which is the worst way for this to fail.
        await SettleAsync(cancellationToken).ConfigureAwait(false);

        var partitionPath = PartitionPath(device.Path, 1);

        var makeArguments = new List<string>();

        if (label is { Length: > 0 })
        {
            makeArguments.AddRange(fileSystem.Id == "xfs" ? ["-L", label] : ["-L", label]);
        }

        // Non-interactive: mkfs asks for confirmation on a device that looks used, and there is no
        // one to answer.
        if (fileSystem.Id is "ext4")
        {
            makeArguments.Add("-F");
        }
        else if (fileSystem.Id is "btrfs" or "xfs")
        {
            makeArguments.Add("-f");
        }

        makeArguments.Add(partitionPath);

        var makeResult = await RunAsync(
                fileSystem.MakeCommand,
                [.. makeArguments],
                $"making a {fileSystem.Name} filesystem",
                device,
                cancellationToken,
                // mkfs on a large disk takes a while, and being killed halfway leaves it unusable.
                TimeSpan.FromMinutes(10))
            .ConfigureAwait(false);

        if (makeResult is not null)
        {
            return makeResult;
        }

        logger.LogInformation("Prepared {Device} as {FileSystem}.", device.Path, fileSystem.Id);

        return new FormatResult
        {
            Succeeded = true,
            Message = $"{device.Name} is ready: one {fileSystem.Name} partition using the whole disk.",
            PartitionPath = partitionPath,
        };
    }

    private async Task<FormatResult?> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        string description,
        BlockDevice device,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        try
        {
            var result = timeout is { } limit
                ? await systemRunner.RunAsync(file, arguments, limit, cancellationToken).ConfigureAwait(false)
                : await systemRunner.RunAsync(file, arguments, cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                return null;
            }

            var detail = result.StandardError.Trim();

            logger.LogError("Failed {Description} on {Device}: {Error}", description, device.Path, detail);

            return new FormatResult
            {
                Succeeded = false,

                // Names the step, because a half-prepared disk needs a different fix depending on
                // how far it got.
                Message = $"Failed while {description}: {(detail.Length > 0 ? detail : $"{file} exited {result.ExitCode}")}",
            };
        }
        catch (SystemOperationException ex)
        {
            return new FormatResult { Succeeded = false, Message = $"Failed while {description}: {ex.Message}" };
        }
    }

    /// <summary>Waits for udev to catch up with the new partition table.</summary>
    private async Task SettleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await systemRunner.RunAsync("udevadm", ["settle"], cancellationToken).ConfigureAwait(false);
        }
        catch (SystemOperationException)
        {
            // No udevadm: fall back to waiting, which is what it would have done anyway.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The path of a partition on a disk.
    /// </summary>
    /// <remarks>
    /// Not simply <c>path + number</c>: devices whose name ends in a digit take a <c>p</c>
    /// separator, so nvme0n1 partition 1 is nvme0n1p1 while sda partition 1 is sda1. Getting this
    /// wrong means mkfs on a path that does not exist — or, worse, one that does.
    /// </remarks>
    internal static string PartitionPath(string diskPath, int number) =>
        char.IsAsciiDigit(diskPath[^1]) ? $"{diskPath}p{number}" : $"{diskPath}{number}";

    /// <summary>
    /// Returns the tools needed for this format that are not installed, in the order they would
    /// have been used.
    /// </summary>
    private Task<List<string>> FindMissingToolsAsync(
        FileSystemChoice fileSystem,
        CancellationToken cancellationToken) =>
        FindMissingToolsAsync([fileSystem], cancellationToken);

    /// <summary>
    /// Returns the tools needed for these filesystems that are not installed, in the order they
    /// would have been used.
    /// </summary>
    private async Task<List<string>> FindMissingToolsAsync(
        IEnumerable<FileSystemChoice> fileSystems,
        CancellationToken cancellationToken)
    {
        var required = new[] { "wipefs", "sgdisk" }
            .Concat(fileSystems.Select(f => f.MakeCommand))
            .Distinct()
            .ToArray();

        var missing = new List<string>();

        foreach (var tool in required)
        {
            try
            {
                var probe = await systemRunner
                    .RunAsync("sh", ["-c", $"command -v {tool}"], cancellationToken)
                    .ConfigureAwait(false);

                if (!probe.Succeeded)
                {
                    missing.Add(tool);
                }
            }
            catch (SystemOperationException)
            {
                // Cannot even probe: treat as missing rather than pressing on and finding out the
                // destructive way.
                missing.Add(tool);
            }
        }

        return missing;
    }

    private static string PackageForTool(string tool) => tool switch
    {
        "sgdisk" => "gdisk",
        "wipefs" => "util-linux",
        "mkfs.xfs" => "xfsprogs",
        "mkfs.btrfs" => "btrfs-progs",
        _ => "e2fsprogs",
    };
}

/// <summary>Outcome of preparing a disk.</summary>
/// <summary>
/// One command a plan will run.
/// </summary>
/// <remarks>
/// Exists so the preview and the run are the same list rather than two descriptions of it that can
/// disagree. <see cref="ToString"/> is what gets shown, so it has to read as the command it is.
/// </remarks>
public sealed record PlannedCommand
{
    /// <summary>What this step is for, in words, used when it fails.</summary>
    public required string Description { get; init; }

    /// <summary>The binary.</summary>
    public required string File { get; init; }

    /// <summary>Its arguments.</summary>
    public required ImmutableArray<string> Arguments { get; init; }

    /// <summary>Whether this one makes a filesystem, which cannot run until the kernel has the table.</summary>
    public bool IsMakeFileSystem => File.StartsWith("mkfs", StringComparison.Ordinal);

    /// <summary>The command as a person would type it.</summary>
    public override string ToString() => $"{File} {string.Join(' ', Arguments)}";
}

public sealed record FormatResult
{
    /// <summary>Whether it worked.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>What happened, in words.</summary>
    public required string Message { get; init; }

    /// <summary>The partition created, when it succeeded.</summary>
    public string? PartitionPath { get; init; }
}
