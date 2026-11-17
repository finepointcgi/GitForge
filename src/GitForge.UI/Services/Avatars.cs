using System.Globalization;

namespace GitForge.UI.Services;

/// <summary>
/// Offline author avatars: initials on a deterministic color. No Gravatar or other network lookups,
/// so author emails never leave the machine.
/// </summary>
public static class Avatars
{
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1)
        {
            var p = parts[0];
            return (p.Length >= 2 ? p[..2] : p).ToUpper(CultureInfo.CurrentCulture);
        }

        return string.Concat(char.ToUpper(parts[0][0], CultureInfo.CurrentCulture), char.ToUpper(parts[^1][0], CultureInfo.CurrentCulture));
    }

    /// <summary>Stable color per author (FNV-1a over the lowercase email).</summary>
    public static string ColorFor(string? email)
    {
        uint hash = 2166136261;
        foreach (var c in (email ?? string.Empty).ToLowerInvariant()) hash = (hash ^ c) * 16777619;
        return GitForgeTheme.LaneColors[hash % (uint)GitForgeTheme.LaneColors.Length];
    }

    public static string LaneColor(int index) => GitForgeTheme.LaneColors[((index % GitForgeTheme.LaneColors.Length) + GitForgeTheme.LaneColors.Length) % GitForgeTheme.LaneColors.Length];

    /// <summary>"3 min ago" for recent commits, otherwise a short date.</summary>
    public static string When(DateTimeOffset when)
    {
        var age = DateTimeOffset.Now - when;
        if (age < TimeSpan.Zero) return when.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} hr ago";
        if (age.TotalDays < 7) return $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? string.Empty : "s")} ago";
        return when.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }
}
