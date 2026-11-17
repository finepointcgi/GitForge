using System.Globalization;
using GitForge.Core.Models;

namespace GitForge.UI.Services;

/// <summary>Parses the history search box: free text plus <c>author:</c>, <c>path:</c>, <c>since:</c>, <c>until:</c> tokens.</summary>
public sealed record HistoryFilter(string? Text, string? Author, string? Path, DateTimeOffset? Since, DateTimeOffset? Until)
{
    public bool IsEmpty => Text is null && Author is null && Path is null && Since is null && Until is null;

    public static HistoryFilter Parse(string query)
    {
        string? author = null, path = null;
        DateTimeOffset? since = null, until = null;
        var text = new List<string>();

        foreach (var token in (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            var key = colon > 0 ? token[..colon].ToLowerInvariant() : null;
            var value = colon > 0 ? token[(colon + 1)..] : token;
            switch (key)
            {
                case "author" when value.Length > 0: author = value; break;
                case "path" when value.Length > 0: path = value; break;
                case "since" when TryDate(value, out var s): since = s; break;
                case "until" when TryDate(value, out var u): until = u.AddDays(1); break;
                default: text.Add(token); break;
            }
        }

        return new HistoryFilter(text.Count > 0 ? string.Join(' ', text) : null, author, path, since, until);
    }

    public bool Matches(CommitInfo c)
    {
        if (Since is { } s && c.AuthorDate < s) return false;
        if (Until is { } u && c.AuthorDate >= u) return false;
        if (Author is { } a && !c.AuthorName.Contains(a, StringComparison.OrdinalIgnoreCase) && !c.AuthorEmail.Contains(a, StringComparison.OrdinalIgnoreCase)) return false;
        if (Text is { } t)
        {
            return c.Subject.Contains(t, StringComparison.OrdinalIgnoreCase)
                || c.Sha.StartsWith(t, StringComparison.OrdinalIgnoreCase)
                || c.AuthorName.Contains(t, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool TryDate(string value, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date);
}
