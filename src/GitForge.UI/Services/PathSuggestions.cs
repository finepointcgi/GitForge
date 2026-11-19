namespace GitForge.UI.Services;

/// <summary>Folder names and checks for new submodules.</summary>
public static class SubmodulePath
{
    /// <summary>"https://host/org/library.git" or "git@host:org/library.git" → "library".</summary>
    public static string Suggest(string url)
    {
        var name = url.Trim().TrimEnd('/', '\\');
        name = name[(Math.Max(name.LastIndexOfAny(['/', '\\', ':']), -1) + 1)..];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Why <paramref name="path"/> can't hold a new submodule, or null when it can.</summary>
    public static string? Validate(string path, string? workDir)
    {
        var p = path.Trim().Replace('\\', '/').Trim('/');
        if (p.Length == 0) return "Required";
        if (p.StartsWith('-') || Path.IsPathRooted(p) || p.Contains(':', StringComparison.Ordinal) || p.Split('/').Any(s => s is "" or "." or ".."))
        {
            return "Use a folder inside this repository, such as libs/name.";
        }

        if (p.Split('/')[0].Equals(".git", StringComparison.OrdinalIgnoreCase)) return "Choose a folder outside .git.";
        if (workDir is not null && Path.Combine(workDir, p) is var full && (File.Exists(full) || (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())))
        {
            return "That folder already exists and isn't empty.";
        }

        return null;
    }
}

/// <summary>Default folders for new worktrees: beside the repository, named after it and the branch.</summary>
public static class WorktreePath
{
    /// <summary>C:\src\app + feature/login → C:\src\app-feature-login.</summary>
    public static string Suggest(string repositoryPath, string branch)
    {
        var repo = repositoryPath.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(repo) ?? repo;
        var safe = new string(branch.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '-' : c).ToArray()).Trim('-', '.');
        return Path.Combine(parent, $"{Path.GetFileName(repo)}-{(safe.Length == 0 ? "worktree" : safe)}");
    }
}
