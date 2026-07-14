namespace EasyHomeServer.Sdk;

/// <summary>
/// Describes a module to the host: identity, presentation and where its UI lives.
/// Returned by <see cref="IModule.Manifest"/> and used to build the navigation menu.
/// </summary>
public sealed record ModuleManifest
{
    /// <summary>
    /// Stable, lowercase identifier. Must be unique across installed modules and is used
    /// for the module's data directory and log scopes. Example: "systeminfo".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name shown in the navigation menu.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Informational module version, e.g. "0.1.0". Not used for compatibility checks.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Route of the module's landing page, matching an <c>@page</c> directive in the module
    /// assembly. Example: "/system".
    /// </summary>
    public required string RoutePath { get; init; }

    /// <summary>
    /// SVG path data for the nav icon. MudBlazor's <c>Icons.Material.*</c> constants are
    /// plain strings and can be passed directly.
    /// </summary>
    public required string Icon { get; init; }

    /// <summary>Ascending sort order in the nav menu. Ties break on <see cref="DisplayName"/>.</summary>
    public int NavOrder { get; init; }

    /// <summary>Optional one-line description shown as a tooltip.</summary>
    public string? Description { get; init; }
}
