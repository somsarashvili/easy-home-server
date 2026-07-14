using System.ComponentModel.DataAnnotations;

namespace EasyHomeServer.Host.Data;

/// <summary>
/// A single persisted host setting. The host's own state is small and mostly scalar, so it
/// lives in one keyed table rather than a table per concern.
/// </summary>
public sealed class Setting
{
    /// <summary>Dotted setting key, for example <c>admin.password</c>.</summary>
    [Key]
    [MaxLength(200)]
    public required string Key { get; set; }

    /// <summary>Opaque value. Interpretation is the caller's business.</summary>
    public required string Value { get; set; }

    /// <summary>When the value was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
