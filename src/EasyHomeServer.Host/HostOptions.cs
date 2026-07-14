using System.ComponentModel.DataAnnotations;

namespace EasyHomeServer.Host;

/// <summary>Host-level settings, bound from the <c>EasyHomeServer</c> configuration section.</summary>
public sealed class EasyHomeServerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "EasyHomeServer";

    /// <summary>
    /// Directory scanned for modules at startup. Each immediate subdirectory is treated as one
    /// published module. Production default is <c>/usr/lib/easyhomeserver/modules</c>; the
    /// development default is <c>./modules</c> relative to the content root.
    /// </summary>
    [Required]
    public string ModulesPath { get; set; } = "modules";

    /// <summary>
    /// Writable root for host and module state (the SQLite database, per-module data
    /// directories). Production default is <c>/var/lib/easyhomeserver</c>.
    /// </summary>
    [Required]
    public string DataPath { get; set; } = "data";
}
