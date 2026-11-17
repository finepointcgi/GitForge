using GitForge.Core.Abstractions;
using GitForge.Git;
using GitForge.Hosting;
using GitForge.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace GitForge.UI.Services;

/// <summary>Dense, high-contrast developer theme. Only the accent color is user-configurable.</summary>
public static class GitForgeTheme
{
    public static readonly string[] LaneColors =
    [
        "#3B82F6", "#22C55E", "#F59E0B", "#EF4444", "#A855F7",
        "#06B6D4", "#EC4899", "#84CC16", "#F97316", "#14B8A6",
    ];

    public static MudTheme Create(string accent) => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = accent,
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
            Primary = accent,
            Secondary = "#8B949E",
            Background = "#0D1117",
            Surface = "#161B22",
            AppbarBackground = "#161B22",
            AppbarText = "#E6EDF3",
            DrawerBackground = "#0D1117",
            DrawerText = "#E6EDF3",
            TextPrimary = "#E6EDF3",
            TextSecondary = "#8B949E",
            LinesDefault = "#30363D",
            Divider = "#30363D",
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
    /// <summary>Registers every GitForge service a host needs. Hosts may override <see cref="IPlatformShell"/>.</summary>
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
        services.AddSingleton(new StartupOptions(null));
        services.AddSingleton<SyntaxHighlighter>();
        return services;
    }
}
