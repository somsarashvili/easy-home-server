using MudBlazor;

namespace EasyHomeServer.Host.Components.Layout;

/// <summary>
/// The shell's MudBlazor theme. Dark is the default, so the dark palette is the one tuned.
/// </summary>
internal static class ShellTheme
{
    /// <summary>Shared theme instance; MudBlazor treats themes as immutable configuration.</summary>
    public static MudTheme Instance { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#4dabf5",
            Secondary = "#64b5a4",
            Background = "#12161c",
            BackgroundGray = "#0d1116",
            Surface = "#1a1f27",
            AppbarBackground = "#161b22",
            AppbarText = "#e6edf3",
            DrawerBackground = "#161b22",
            DrawerText = "#c9d1d9",
            DrawerIcon = "#8b949e",
            TextPrimary = "#e6edf3",
            TextSecondary = "#8b949e",
            Divider = "#2c333b",
            Success = "#3fb950",
            Warning = "#d29922",
            Error = "#f85149",
            Info = "#4dabf5",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
        Typography = new Typography
        {
            // Self-hosted stack only: a home server must render correctly with no internet,
            // so the theme never reaches for a webfont CDN.
            Default = new DefaultTypography
            {
                FontFamily = ["system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica", "Arial", "sans-serif"],
            },
        },
    };
}
