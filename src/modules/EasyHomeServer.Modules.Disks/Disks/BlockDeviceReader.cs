using System.Collections.Immutable;
using System.Text.Json;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>
/// Reads the machine's block devices, via <c>lsblk</c>.
/// </summary>
/// <remarks>
/// <para>
/// lsblk rather than walking <c>/sys/block</c> by hand, unlike the SystemInfo module's approach to
/// <c>/proc</c>. The difference is that sysfs does not have the answers: filesystem type, label,
/// UUID and free space come from blkid and statfs, and the parent/child structure has to be
/// reassembled from symlinks. lsblk already does all of that, ships with the base system, and
/// emits JSON.
/// </para>
/// <para>
/// <c>-b</c> matters: without it sizes arrive as "64G" and precision is gone before it is read.
/// </para>
/// <para>
/// Run inside PID 1's mount namespace, for the same reason SystemInfo reads /proc/1/mountinfo: the
/// unit sets <c>StateDirectory=</c> and <c>PrivateTmp=</c>, so this process has bind mounts nobody
/// else has. Run plainly, lsblk reports the root partition as mounted at <c>/var/tmp</c> — its own
/// sandbox, listed first — which is both wrong and alarming. PID 1's view is the machine's.
/// </para>
/// </remarks>
public sealed class BlockDeviceReader(ISystemRunner systemRunner, ILogger<BlockDeviceReader> logger)
{
    /// <summary>Fields asked of lsblk. Named explicitly so a new lsblk adding columns cannot change what is parsed.</summary>
    private const string Columns =
        "NAME,PATH,TYPE,SIZE,MODEL,SERIAL,VENDOR,TRAN,PTTYPE,ROTA,RM,RO,FSTYPE,LABEL,UUID,MOUNTPOINTS,FSSIZE,FSAVAIL,FSUSED";

    /// <summary>Whether lsblk is present. It is part of util-linux, so on Debian it always is.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await systemRunner.RunAsync("lsblk", ["--version"], cancellationToken).ConfigureAwait(false);

            return result.Succeeded;
        }
        catch (SystemOperationException)
        {
            return false;
        }
    }

    /// <summary>Reads every block device, as a tree of disks and their partitions.</summary>
    public async Task<ImmutableArray<BlockDevice>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunLsblkAsync(cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                return [];
            }

            using var document = JsonDocument.Parse(result);

            if (!document.RootElement.TryGetProperty("blockdevices", out var devices)
                || devices.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var builder = ImmutableArray.CreateBuilder<BlockDevice>();

            foreach (var element in devices.EnumerateArray())
            {
                if (Parse(element) is { } device)
                {
                    builder.Add(device);
                }
            }

            return builder.ToImmutable();
        }
        catch (Exception ex) when (ex is JsonException or SystemOperationException)
        {
            logger.LogError(ex, "Could not read block devices.");

            return [];
        }
    }

    /// <summary>
    /// Runs lsblk in the machine's mount namespace, falling back to this one.
    /// </summary>
    /// <remarks>
    /// nsenter needs privileges and a PID 1 to enter, so it is unavailable in a container where
    /// this process may be PID 1 already. Falling back is correct there: with no separate
    /// namespace to escape, the plain view is the right one.
    /// </remarks>
    private async Task<string?> RunLsblkAsync(CancellationToken cancellationToken)
    {
        string[] lsblkArguments = ["--json", "--bytes", "--output", Columns];

        try
        {
            var entered = await systemRunner
                .RunAsync("nsenter", ["--target", "1", "--mount", "lsblk", .. lsblkArguments], cancellationToken)
                .ConfigureAwait(false);

            if (entered.Succeeded)
            {
                return entered.StandardOutput;
            }

            logger.LogDebug(
                "Could not read block devices from PID 1's namespace ({Error}); reading this process's view instead.",
                entered.StandardError.Trim());
        }
        catch (SystemOperationException)
        {
            // nsenter absent — fall through.
        }

        try
        {
            var plain = await systemRunner.RunAsync("lsblk", lsblkArguments, cancellationToken).ConfigureAwait(false);

            if (plain.Succeeded)
            {
                return plain.StandardOutput;
            }

            logger.LogWarning("lsblk failed: {Error}", plain.StandardError.Trim());

            return null;
        }
        catch (SystemOperationException ex)
        {
            logger.LogError(ex, "Could not run lsblk.");

            return null;
        }
    }

    private BlockDevice? Parse(JsonElement element)
    {
        var name = GetString(element, "name");

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var children = ImmutableArray<BlockDevice>.Empty;

        if (element.TryGetProperty("children", out var childArray) && childArray.ValueKind == JsonValueKind.Array)
        {
            var builder = ImmutableArray.CreateBuilder<BlockDevice>();

            foreach (var child in childArray.EnumerateArray())
            {
                if (Parse(child) is { } parsed)
                {
                    builder.Add(parsed);
                }
            }

            children = builder.ToImmutable();
        }

        return new BlockDevice
        {
            Name = name,
            Path = GetString(element, "path") ?? $"/dev/{name}",
            Kind = ParseKind(GetString(element, "type")),
            SizeBytes = GetLong(element, "size") ?? 0,
            Model = Clean(GetString(element, "model")),
            Serial = Clean(GetString(element, "serial")),
            Vendor = Clean(GetString(element, "vendor")),
            Transport = Clean(GetString(element, "tran")),
            PartitionTable = Clean(GetString(element, "pttype")),
            IsRotational = GetBool(element, "rota") ?? false,
            IsRemovable = GetBool(element, "rm") ?? false,
            IsReadOnly = GetBool(element, "ro") ?? false,
            FileSystem = Clean(GetString(element, "fstype")),
            Label = Clean(GetString(element, "label")),
            Uuid = Clean(GetString(element, "uuid")),
            MountPoints = ParseMountPoints(element),
            FsSizeBytes = GetLong(element, "fssize"),
            FsAvailableBytes = GetLong(element, "fsavail"),
            FsUsedBytes = GetLong(element, "fsused"),
            Children = children,
        };
    }

    private static DeviceKind ParseKind(string? type) => type switch
    {
        "disk" => DeviceKind.Disk,
        "part" => DeviceKind.Partition,
        "rom" => DeviceKind.Rom,
        "loop" => DeviceKind.Loop,
        "lvm" => DeviceKind.Lvm,
        "crypt" => DeviceKind.Crypt,
        "raid0" or "raid1" or "raid5" or "raid6" or "raid10" => DeviceKind.Raid,
        _ => DeviceKind.Other,
    };

    /// <summary>
    /// Reads the mountpoints array, dropping the nulls lsblk puts there for unmounted devices.
    /// </summary>
    private static ImmutableArray<string> ParseMountPoints(JsonElement element)
    {
        if (!element.TryGetProperty("mountpoints", out var mountPoints)
            || mountPoints.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var item in mountPoints.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path)
            {
                builder.Add(path);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Trims a field and turns an empty one into null.
    /// </summary>
    /// <remarks>
    /// lsblk pads model and vendor to a fixed width from the SCSI inquiry, so "QEMU HARDDISK   "
    /// arrives with trailing spaces.
    /// </remarks>
    private static string? Clean(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
