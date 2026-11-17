using GitForge.Core.Abstractions;
using GitForge.Platform.Settings;
using GitForge.UI.Services;

namespace GitForge.UI.Tests;

public class ThemeTests
{
    [Fact]
    public void DefaultAccent_IsGreen_AndDarkerInLightMode()
    {
        var theme = GitForgeTheme.Create(AppSettings.DefaultAccentColor);

        Assert.Equal(AppSettings.DefaultAccentColor, theme.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(GitForgeTheme.LightBrandAccent, theme.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomAccent_IsUsedInBothModes_AndInvalidFallsBackToDefault()
    {
        var custom = GitForgeTheme.Create("#3366CC");
        Assert.Equal("#3366CC", custom.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("#3366CC", custom.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), StringComparer.OrdinalIgnoreCase);

        var invalid = GitForgeTheme.Create("green");
        Assert.Equal(AppSettings.DefaultAccentColor, invalid.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("#86D24A", "#0A120E")] // GitForge green: dark text (10:1)
    [InlineData("#1E7B34", "#FFFFFF")] // light-mode green: white text (5.3:1)
    [InlineData("#3B82F6", "#FFFFFF")] // the old blue keeps white text
    public void ContrastText_PicksReadableTextForAccentFills(string accent, string expected) =>
        Assert.Equal(expected, GitForgeTheme.ContrastText(accent));

    [Theory]
    [InlineData("#3B82F6", AppSettings.DefaultAccentColor)] // old default (never customized) follows the new default
    [InlineData("#3b82f6", AppSettings.DefaultAccentColor)]
    [InlineData("#AA5500", "#AA5500")] // a chosen accent is kept
    public async Task SettingsLoad_MigratesOnlyTheOldDefaultAccent(string stored, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitforge-settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, $$"""{ "theme": "Dark", "accentColor": "{{stored}}" }""");
            using var store = new JsonSettingsStore(path);

            var settings = await store.LoadAsync();

            Assert.Equal(expected, settings.AccentColor);
            Assert.Equal(ThemePreference.Dark, settings.Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
