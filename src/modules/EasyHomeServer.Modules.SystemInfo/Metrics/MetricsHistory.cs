using System.Collections.Immutable;

namespace EasyHomeServer.Modules.SystemInfo.Metrics;

/// <summary>
/// A fixed-size ring of recent samples, backing the sparklines. Bounded on purpose: this runs
/// for months on a box with modest RAM, so history has a hard ceiling rather than growing with
/// uptime.
/// </summary>
/// <remarks>
/// At the default 2-second interval, 90 entries is three minutes of history for a few hundred
/// bytes. Reads take a snapshot under the same lock writes use, so a component rendering while
/// the sampler writes can never observe a torn buffer.
/// </remarks>
public sealed class MetricsHistory
{
    /// <summary>Number of samples retained.</summary>
    public const int Capacity = 90;

    private readonly Lock _gate = new();
    private readonly SystemSnapshot?[] _buffer = new SystemSnapshot?[Capacity];
    private int _next;
    private int _count;

    /// <summary>The most recent sample, or null before the first one arrives.</summary>
    public SystemSnapshot? Latest { get; private set; }

    /// <summary>Appends a sample, evicting the oldest once at capacity.</summary>
    public void Add(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            _buffer[_next] = snapshot;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
            Latest = snapshot;
        }
    }

    /// <summary>Returns the retained samples, oldest first.</summary>
    public ImmutableArray<SystemSnapshot> Snapshot()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return [];
            }

            var builder = ImmutableArray.CreateBuilder<SystemSnapshot>(_count);
            var start = (_next - _count + Capacity) % Capacity;

            for (var i = 0; i < _count; i++)
            {
                if (_buffer[(start + i) % Capacity] is { } snapshot)
                {
                    builder.Add(snapshot);
                }
            }

            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// Projects one numeric series out of the retained samples, for a sparkline. Samples where
    /// the value is unavailable contribute 0 so the series stays aligned with time.
    /// </summary>
    public double[] Series(Func<SystemSnapshot, double?> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        return [.. Snapshot().Select(s => selector(s) ?? 0)];
    }
}
