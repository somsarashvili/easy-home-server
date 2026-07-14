using System.Globalization;

namespace EasyHomeServer.Modules.Docker;

/// <summary>
/// Display formatting for this module.
/// </summary>
/// <remarks>
/// Deliberately duplicated from the SystemInfo module rather than shared. Putting it in the SDK
/// would make every helper here a permanent compatibility commitment across all modules, to save
/// forty lines. The SDK is the plugin contract, not a utility library.
/// </remarks>
internal static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count in binary units, matching what <c>docker images</c> reports.</summary>
    public static string Bytes(long value)
    {
        if (value <= 0)
        {
            return "0 B";
        }

        var unit = 0;
        double size = value;

        // Rounded comparison, so 1023.96 MB promotes to 1.0 GB rather than displaying "1024.0 MB".
        while (Math.Round(size, 1) >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = unit <= 1 ? "0" : "0.0";

        return $"{size.ToString(format, CultureInfo.InvariantCulture)} {ByteUnits[unit]}";
    }

    /// <summary>Formats a duration the way <c>docker ps</c> does: coarse, and never more than two units.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}d {value.Hours}h";
        }

        if (value.TotalHours >= 1)
        {
            return $"{value.Hours}h {value.Minutes}m";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{value.Minutes}m {value.Seconds}s";
        }

        return $"{value.Seconds}s";
    }

    /// <summary>Formats a timestamp as an age, for example "3 days ago".</summary>
    public static string Ago(DateTimeOffset? value)
    {
        if (value is not { } timestamp)
        {
            return "—";
        }

        var elapsed = DateTimeOffset.UtcNow - timestamp;

        // Clock skew or a container created "in the future" should not render as "-3s ago".
        if (elapsed < TimeSpan.Zero)
        {
            return "just now";
        }

        return $"{Duration(elapsed)} ago";
    }
}
