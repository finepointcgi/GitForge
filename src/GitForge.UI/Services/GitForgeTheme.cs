using System.Buffers;
using System.Globalization;
using GitForge.Core.Abstractions;
using GitForge.Git;
using GitForge.Hosting;
using GitForge.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace GitForge.UI.Services;

/// <summary>
/// Dense, high-contrast developer theme with the forest greens of the GitForge logo in dark mode. Only the accent
/// color is user-configurable; dark-mode surfaces must match <c>.gf-shell.dark</c> in gitforge.css.
/// </summary>
public static class GitForgeTheme
{
    /// <summary>Light-mode rendering of the default accent: the logo's deep green, 5.3:1 on white.</summary>
    public const string LightBrandAccent = "#1E7B34";

    public static readonly string[] LaneColors =
    [
        "#3B82F6", "#22C55E", "#F59E0B", "#EF4444", "#A855F7",
        "#06B6D4", "#EC4899", "#84CC16", "#F97316", "#14B8A6",
    ];

    public static MudTheme Create(string accent)
    {
        var dark = IsHexColor(accent) ? accent : AppSettings.DefaultAccentColor;
        var light = string.Equals(dark, AppSettings.DefaultAccentColor, StringComparison.OrdinalIgnoreCase) ? LightBrandAccent : dark;
        return Create(light, dark);
    }

    /// <summary>Text color for content drawn on <paramref name="hex"/>: near-black on light colors, white otherwise.</summary>
    public static string ContrastText(string hex) => RelativeLuminance(hex) > 0.4 ? "#0A120E" : "#FFFFFF";

    /// <summary>WCAG relative luminance of a #RRGGBB color.</summary>
    public static double RelativeLuminance(string hex)
    {
        if (!IsHexColor(hex)) throw new ArgumentException($"Expected #RRGGBB, got '{hex}'.", nameof(hex));
        static double Channel(string hex, int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(hex, 1)) + (0.7152 * Channel(hex, 3)) + (0.0722 * Channel(hex, 5));
    }

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    private static bool IsHexColor(string? s) =>
        s is { Length: 7 } && s[0] == '#' && !s.AsSpan(1).ContainsAnyExcept(HexDigits);

    private static MudTheme Create(string lightAccent, string darkAccent) => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = lightAccent,
            PrimaryContrastText = ContrastText(lightAccent),
            Secondary = "#6E7781",
            Background = "#FFFFFF",
            Surface = "#FFFFFF",
            AppbarBackground = "#F6F8FA",
            AppbarText = "#1F2328",
            DrawerBackground = "#F6F8FA",
            DrawerText = "#1F2328",
            TextPrimary = "#1F2328",
            TextSecondary = "#59636E",
            LinesDefault = "#D1D9E0",
            Divider = "#D1D9E0",
            Success = "#1A7F37",
            Error = "#CF222E",
            Warning = "#9A6700",
        },
        PaletteDark = new PaletteDark
        {
            Primary = darkAccent,
            PrimaryContrastText = ContrastText(darkAccent),
            Secondary = "#8AA293",
            Background = "#0A120E",
            Surface = "#0E1813",
            BackgroundGray = "#122019",
            AppbarBackground = "#122019",
            AppbarText = "#E3EFE6",
            DrawerBackground = "#0A120E",
            DrawerText = "#E3EFE6",
            TextPrimary = "#E3EFE6",
            TextSecondary = "#8AA293",
            ActionDefault = "#8AA293",
            LinesDefault = "#22382B",
            LinesInputs = "#2E4A39",
            Divider = "#22382B",
            TableLines = "#22382B",
            Success = "#3FB950",
            Error = "#F85149",
            Warning = "#D29922",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI Variable Text", "Segoe UI", "Inter", "system-ui", "sans-serif"],
                FontSize = "0.8125rem",
            },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" },
    };
}

public static class UiServiceCollectionExtensions
{
    /// <summary>Registers every GitForge service a host needs. Hosts may override <see cref="IPlatformShell"/> and <see cref="IAppUpdater"/>.</summary>
    public static IServiceCollection AddGitForge(this IServiceCollection services)
    {
        services.AddMudServices(c =>
        {
            c.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            c.SnackbarConfiguration.VisibleStateDuration = 4000;
            c.SnackbarConfiguration.PreventDuplicates = true;
        });
        services.AddGitForgeGit();
        services.AddGitForgePlatform();
        services.AddGitForgeHosting();
        services.AddScoped<RepoWorkspace>();
        services.AddScoped<ShortcutService>();
        services.AddSingleton<IPlatformShell, DefaultPlatformShell>();
        services.AddSingleton<IAppUpdater, NoAppUpdater>(); // the desktop app replaces this with Velopack
        services.AddScoped<UpdateService>();
        services.AddSingleton(new StartupOptions(null));
        services.AddSingleton<SyntaxHighlighter>();
        return services;
    }
}
