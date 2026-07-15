using System.Globalization;

namespace EasyHomeServer.Modules.Disks;

/// <summary>
/// Display formatting for this module.
/// </summary>
/// <remarks>
/// Duplicated across modules rather than shared, as with the others: putting it in the SDK would
/// make every helper here a permanent compatibility commitment for every module ever written, to
/// save a few dozen lines.
/// </remarks>
internal static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count in binary units, matching what <c>lsblk</c> and <c>df -h</c> print for
    /// the same device.
    /// </summary>
    public static string Bytes(long? value)
    {
        if (value is not { } bytes || bytes <= 0)
        {
            return value is 0 ? "0 B" : "—";
        }

        var unit = 0;
        double size = bytes;

        // Rounded comparison, so 1023.96 MB promotes to 1.0 GB rather than reading "1024.0 MB".
        while (Math.Round(size, 1) >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = unit <= 1 ? "0" : "0.0";

        return $"{size.ToString(format, CultureInfo.InvariantCulture)} {ByteUnits[unit]}";
    }

    /// <summary>Formats a percentage to one decimal place.</summary>
    public static string Percent(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Turns a disk's power-on hours into something a person can judge.
    /// </summary>
    /// <remarks>
    /// 43,000 means nothing; "4.9 years" means the disk is older than most warranties. The raw
    /// figure is kept alongside because that is what every other SMART tool prints.
    /// </remarks>
    public static string PowerOnHours(int hours)
    {
        var years = hours / 8760.0;

        if (years >= 1)
        {
            return $"{years.ToString("0.0", CultureInfo.InvariantCulture)} years ({hours:N0} hours)";
        }

        var days = hours / 24.0;

        return days >= 1
            ? $"{days.ToString("0", CultureInfo.InvariantCulture)} days ({hours:N0} hours)"
            : $"{hours} hours";
    }
}
