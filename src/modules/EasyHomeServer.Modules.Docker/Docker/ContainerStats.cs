using System.Globalization;

namespace EasyHomeServer.Modules.Docker.Docker;

/// <summary>
/// A live resource sample for one container, from <c>docker stats</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only the percentages are parsed into numbers. The byte figures are kept as the strings Docker
/// produced — "7.195MiB / 7.745GiB", "5.6kB / 3.9kB" — deliberately: Docker has already rounded
/// them, and mixes binary units for memory with decimal ones for I/O. Parsing those back into
/// bytes and reformatting would invent precision that is not there and risk disagreeing with what
/// <c>docker stats</c> shows for the same container. Percentages are unambiguous and are needed as
/// numbers to draw a bar.
/// </para>
/// <para>
/// Not carried on <see cref="DockerSnapshot"/>: <c>docker stats</c> takes roughly a second and a
/// half whatever you ask it for, because it samples twice to compute a rate. Putting that in the
/// poll loop would spend half of every interval on numbers nobody is looking at.
/// </para>
/// </remarks>
public sealed record ContainerStats
{
    /// <summary>CPU use across all cores, as a percentage. Can exceed 100 on a multi-core host.</summary>
    public required double CpuPercent { get; init; }

    /// <summary>Memory use as a percentage of the container's limit.</summary>
    public required double MemoryPercent { get; init; }

    /// <summary>Memory used and the limit, as Docker formats it.</summary>
    public required string MemoryUsage { get; init; }

    /// <summary>Bytes received and sent on the network, as Docker formats it.</summary>
    public required string NetworkIo { get; init; }

    /// <summary>Bytes read and written to block devices, as Docker formats it.</summary>
    public required string BlockIo { get; init; }

    /// <summary>Number of processes or threads in the container.</summary>
    public required int Processes { get; init; }

    /// <summary>When the sample was taken.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Parses a percentage as Docker writes it, for example "0.42%".
    /// </summary>
    /// <remarks>
    /// Returns 0 rather than throwing on anything unexpected: a stats line that cannot be read is
    /// worth showing as zero, not worth blanking the page for.
    /// </remarks>
    internal static double ParsePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var trimmed = value.Trim().TrimEnd('%');

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
