namespace GitForge.Core.Validation;

/// <summary>
/// Client-side implementation of <c>git check-ref-format</c> rules for branch and tag names.
/// Also rejects names beginning with '-' so user input can never be parsed as a CLI option.
/// </summary>
public static class RefNameValidator
{
    private static readonly System.Buffers.SearchValues<char> Forbidden = System.Buffers.SearchValues.Create(" ~^:?*[\\\x7f");

    public static bool IsValid(string? name, out string? error)
    {
        error = Validate(name);
        return error is null;
    }

    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (name.StartsWith('-')) return "Name cannot start with '-'.";
        if (name == "@") return "Name cannot be '@'.";
        if (name.StartsWith('/') || name.EndsWith('/')) return "Name cannot start or end with '/'.";
        if (name.EndsWith('.')) return "Name cannot end with '.'.";
        if (name.Contains("..", StringComparison.Ordinal)) return "Name cannot contain '..'.";
        if (name.Contains("//", StringComparison.Ordinal)) return "Name cannot contain '//'.";
        if (name.Contains("@{", StringComparison.Ordinal)) return "Name cannot contain '@{'.";
        if (name.AsSpan().IndexOfAny(Forbidden) >= 0) return "Name contains a forbidden character (space ~ ^ : ? * [ \\).";
        if (name.Any(char.IsControl)) return "Name cannot contain control characters.";

        foreach (var component in name.Split('/'))
        {
            if (component.StartsWith('.')) return "Path components cannot start with '.'.";
            if (component.EndsWith(".lock", StringComparison.Ordinal)) return "Path components cannot end with '.lock'.";
        }

        return null;
    }
}
