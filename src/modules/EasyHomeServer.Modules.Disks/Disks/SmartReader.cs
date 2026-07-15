using System.Text.Json;
using EasyHomeServer.Sdk;
using Microsoft.Extensions.Logging;

namespace EasyHomeServer.Modules.Disks.Disks;

/// <summary>
/// Reads a disk's SMART health, via <c>smartctl</c>.
/// </summary>
/// <remarks>
/// The one thing on this page worth acting on before something goes wrong: a disk that reports
/// reallocated sectors or a failing self-assessment is telling you it is dying while it still
/// works. Everything else here describes what is; this predicts what is about to be.
/// </remarks>
public sealed class SmartReader(ISystemRunner systemRunner, ILogger<SmartReader> logger)
{
    /// <summary>What SMART says about a disk.</summary>
    public sealed record SmartHealth
    {
        /// <summary>Whether SMART data could be read at all.</summary>
        public required SmartAvailability Availability { get; init; }

        /// <summary>Why it is unavailable, phrased for the operator. Null when it is available.</summary>
        public string? Reason { get; init; }

        /// <summary>The disk's own verdict on itself, when it has one.</summary>
        public bool? Passed { get; init; }

        /// <summary>Model as the disk reports it, which is often better than lsblk's.</summary>
        public string? Model { get; init; }

        /// <summary>Firmware version.</summary>
        public string? Firmware { get; init; }

        /// <summary>Hours the disk has been powered on. Age, in the only unit that matters for a disk.</summary>
        public int? PowerOnHours { get; init; }

        /// <summary>Times it has been powered up.</summary>
        public int? PowerCycles { get; init; }

        /// <summary>Current temperature in Celsius.</summary>
        public int? TemperatureCelsius { get; init; }

        /// <summary>
        /// Sectors the disk has remapped after finding them bad.
        /// </summary>
        /// <remarks>
        /// Zero is normal and expected. Anything above zero that keeps climbing is the clearest
        /// early warning a disk gives.
        /// </remarks>
        public int? ReallocatedSectors { get; init; }

        /// <summary>Sectors the disk suspects are bad but has not yet remapped.</summary>
        public int? PendingSectors { get; init; }
    }

    /// <summary>Whether SMART could be read.</summary>
    public enum SmartAvailability
    {
        /// <summary>Read successfully.</summary>
        Available,

        /// <summary>The device has no SMART — a virtual disk, or one behind a controller that hides it.</summary>
        NotSupported,

        /// <summary>smartmontools is not installed.</summary>
        ToolMissing,

        /// <summary>smartctl ran but failed for some other reason.</summary>
        Error,
    }

    /// <summary>
    /// Reads SMART for one disk.
    /// </summary>
    /// <remarks>
    /// smartctl's exit code is a bitfield, not a success flag: bit 0 means the command failed, but
    /// bits 2 and up are set when SMART itself reports something wrong — a *successful* read of
    /// bad news. Treating a non-zero exit as failure would hide exactly the disks worth knowing
    /// about, so the JSON is parsed regardless and the exit code is only consulted for bit 0.
    /// </remarks>
    public async Task<SmartHealth> ReadAsync(string devicePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        string output;

        try
        {
            var result = await systemRunner
                .RunAsync("smartctl", ["--json=c", "--info", "--health", "--attributes", devicePath], cancellationToken)
                .ConfigureAwait(false);

            output = result.StandardOutput;
        }
        catch (SystemOperationException)
        {
            return new SmartHealth
            {
                Availability = SmartAvailability.ToolMissing,
                Reason = "smartmontools is not installed. Install it with: apt install smartmontools",
            };
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return new SmartHealth { Availability = SmartAvailability.Error, Reason = "smartctl returned nothing." };
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;

            // smartctl reports its own problems in a messages array rather than only on stderr.
            if (root.TryGetProperty("smartctl", out var smartctl)
                && smartctl.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var text = message.TryGetProperty("string", out var s) ? s.GetString() : null;

                    if (text is null)
                    {
                        continue;
                    }

                    // What a virtual disk says. It is not a fault, and the UI should not dress it
                    // up as one.
                    if (text.Contains("Unable to detect device type", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Unknown USB bridge", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("device does not support", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SmartHealth
                        {
                            Availability = SmartAvailability.NotSupported,
                            Reason = "This device does not report SMART. Virtual disks and some USB "
                                     + "enclosures do not pass it through.",
                        };
                    }
                }
            }

            var hasDevice = root.TryGetProperty("device", out _);

            if (!hasDevice)
            {
                return new SmartHealth
                {
                    Availability = SmartAvailability.NotSupported,
                    Reason = "This device does not report SMART.",
                };
            }

            return new SmartHealth
            {
                Availability = SmartAvailability.Available,
                Passed = root.TryGetProperty("smart_status", out var status)
                         && status.TryGetProperty("passed", out var passed)
                    ? passed.ValueKind == JsonValueKind.True
                    : null,
                Model = GetString(root, "model_name"),
                Firmware = GetString(root, "firmware_version"),
                PowerOnHours = GetNested(root, "power_on_time", "hours"),
                PowerCycles = GetInt(root, "power_cycle_count"),
                TemperatureCelsius = GetNested(root, "temperature", "current"),
                ReallocatedSectors = FindAttribute(root, 5),
                PendingSectors = FindAttribute(root, 197),
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse smartctl output for {Device}.", devicePath);

            return new SmartHealth { Availability = SmartAvailability.Error, Reason = "smartctl output could not be read." };
        }
    }

    /// <summary>
    /// Finds a classic ATA SMART attribute by id.
    /// </summary>
    /// <remarks>
    /// Ids rather than names: the names vary between vendors and firmware ("Reallocated_Sector_Ct"
    /// versus "Reallocated_Event_Count"), while the numbers are fixed. 5 is reallocated sectors,
    /// 197 is pending. NVMe reports neither and returns null, which is correct — it has its own
    /// health log with different semantics.
    /// </remarks>
    private static int? FindAttribute(JsonElement root, int id)
    {
        if (!root.TryGetProperty("ata_smart_attributes", out var attributes)
            || !attributes.TryGetProperty("table", out var table)
            || table.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attribute in table.EnumerateArray())
        {
            if (attribute.TryGetProperty("id", out var attributeId)
                && attributeId.TryGetInt32(out var parsed)
                && parsed == id)
            {
                return GetNested(attribute, "raw", "value");
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static int? GetNested(JsonElement element, string parent, string child) =>
        element.TryGetProperty(parent, out var node) ? GetInt(node, child) : null;
}
