using System.Globalization;

namespace EasyHomeServer.Modules.SystemInfo;

/// <summary>Display formatting for metric values.</summary>
internal static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count in binary units (1 KB = 1024 B), matching what <c>free</c> and
    /// <c>df -h</c> report on the same machine.
    /// </summary>
    public static string Bytes(long value)
    {
        if (value <= 0)
        {
            return "0 B";
        }

        var unit = 0;
        double size = value;

        // Compare the *rounded* value against the threshold, not the raw one. 1048572 kB of
        // swap is 1023.996 MiB: rounding to one decimal displays "1024.0 MB", which is both
        // wrong-looking and a unit late. Promoting on the rounded value shows "1.0 GB".
        while (Math.Round(size, 1) >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Bytes and KB are never fractional; larger units read better with one decimal.
        var format = unit <= 1 ? "0" : "0.0";

        return string.Create(CultureInfo.InvariantCulture, $"{size.ToString(format, CultureInfo.InvariantCulture)} {ByteUnits[unit]}");
    }

    /// <summary>Formats a byte-per-second rate.</summary>
    public static string Rate(double bytesPerSecond)
    {
        return $"{Bytes((long)Math.Round(bytesPerSecond))}/s";
    }

    /// <summary>Formats a percentage to one decimal place.</summary>
    public static string Percent(double value)
    {
        return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// Formats an uptime the way an operator reads it: days and hours for a long-lived server,
    /// down to seconds for one that just came back.
    /// </summary>
    public static string Uptime(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            var days = (int)value.TotalDays;

            return $"{days}d {value.Hours}h {value.Minutes}m";
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

    /// <summary>Formats a load average.</summary>
    public static string Load(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
