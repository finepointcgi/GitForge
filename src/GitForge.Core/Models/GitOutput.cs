using System.Text.RegularExpressions;

namespace GitForge.Core.Models;

/// <summary>Parsers for the git output GitForge acts on. Callers run git with <c>LC_ALL=C</c> where messages are parsed.</summary>
public static partial class GitOutput
{
    /// <summary>
    /// Submodule paths named by a push that <c>--recurse-submodules=check</c> refused:
    /// "The following submodule paths contain changes that can\nnot be found on any remote:\n  libs/sub\n\nPlease try…".
    /// Empty when the message is about something else.
    /// </summary>
    public static IReadOnlyList<string> SubmodulesMissingOnRemote(string message)
    {
        const string marker = "not be found on any remote:";
        var at = message.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return [];

        var paths = new List<string>();
        foreach (var raw in message[(at + marker.Length)..].Split('\n').Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                if (paths.Count > 0) break;
                continue;
            }

            if (line[0] is not (' ' or '\t')) break;
            paths.Add(line.Trim());
        }

        return paths;
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c> (newline- or, with <c>-z</c>, NUL-terminated). The first entry is the
    /// main worktree; <paramref name="currentWorkDir"/> marks the one this repository was opened from.
    /// </summary>
    public static IReadOnlyList<WorktreeInfo> ParseWorktrees(string porcelain, string currentWorkDir)
    {
        var list = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null, lockReason = null;
        bool bare = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is not null)
            {
                var full = Normalize(path);
                list.Add(new WorktreeInfo(full, head, branch, list.Count == 0, SamePath(full, currentWorkDir), bare, lockReason, locked, prunable));
            }

            path = head = branch = lockReason = null;
            bare = locked = prunable = false;
        }

        var separator = porcelain.Contains('\0', StringComparison.Ordinal) ? '\0' : '\n';
        foreach (var raw in porcelain.Split(separator))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? null : line[(space + 1)..];
            switch (key)
            {
                case "worktree":
                    Flush();
                    path = value;
                    break;
                case "HEAD":
                    head = value;
                    break;
                case "branch":
                    branch = value is not null && value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value;
                    break;
                case "bare":
                    bare = true;
                    break;
                case "locked":
                    locked = true;
                    lockReason = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "prunable":
                    prunable = true;
                    break;
            }
        }

        Flush();
        return list;
    }

    /// <summary>True when two paths name the same folder (case-insensitive on Windows, separators normalized).</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// A mailbox from <c>git format-patch</c> (or an email) rather than a plain diff: <c>git am</c> applies it as commits
    /// with their authors and messages, where <c>git apply</c> would only change files.
    /// </summary>
    public static bool IsMailbox(string text) =>
        text.StartsWith("From ", StringComparison.Ordinal) && text.Contains("\nSubject: ", StringComparison.Ordinal);

    /// <summary>
    /// Bisect state from <c>git for-each-ref --format="%(refname) %(objectname)" refs/bisect/</c> and the bisect log
    /// (which records "# first bad commit: [sha] subject" once the search ends).
    /// </summary>
    public static BisectStatus ParseBisect(string refs, string? log, int? remaining)
    {
        string? bad = null;
        var good = new List<string>();
        var skipped = 0;
        foreach (var raw in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(' ');
            if (parts.Length < 2) continue;
            if (parts[0] == "refs/bisect/bad") bad = parts[1];
            else if (parts[0].StartsWith("refs/bisect/good-", StringComparison.Ordinal)) good.Add(parts[1]);
            else if (parts[0].StartsWith("refs/bisect/skip-", StringComparison.Ordinal)) skipped++;
        }

        string? firstBad = null;
        foreach (var line in (log ?? string.Empty).Split('\n'))
        {
            var m = FirstBadInLog().Match(line);
            if (m.Success) firstBad = m.Groups[1].Value;
        }

        return new BisectStatus(bad, good, skipped, firstBad, firstBad is null ? remaining : 0);
    }

    /// <summary>
    /// A submodule pointer change: git diffs a submodule as "-Subproject commit &lt;old&gt;" / "+Subproject commit &lt;new&gt;[-dirty]".
    /// Null when <paramref name="diff"/> is an ordinary file.
    /// </summary>
    public static SubmoduleChange? SubmoduleChange(Diff.FileDiff? diff)
    {
        const string prefix = "Subproject commit ";
        var lines = diff?.Hunks.SelectMany(h => h.Lines).Where(l => l.IsChange).ToList();
        if (lines is not { Count: > 0 } || !lines.All(l => l.DisplayText.StartsWith(prefix, StringComparison.Ordinal))) return null;

        string? old = null, @new = null;
        var dirty = false;
        foreach (var l in lines)
        {
            var sha = l.DisplayText[prefix.Length..].Trim();
            if (l.Kind == Diff.DiffLineKind.Deleted)
            {
                old = sha.Replace("-dirty", string.Empty, StringComparison.Ordinal);
            }
            else
            {
                dirty |= sha.EndsWith("-dirty", StringComparison.Ordinal);
                @new = sha.Replace("-dirty", string.Empty, StringComparison.Ordinal);
            }
        }

        return new SubmoduleChange(old, @new, dirty);
    }

    /// <summary>The commit named by "&lt;sha&gt; is the first bad commit" in <c>git bisect good/bad</c> output, or null.</summary>
    public static string? FirstBadCommit(string output) =>
        FirstBadInOutput().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^# first bad commit: \[([0-9a-f]{40,64})\]")]
    private static partial Regex FirstBadInLog();

    [GeneratedRegex(@"^([0-9a-f]{40,64}) is the first bad commit", RegexOptions.Multiline)]
    private static partial Regex FirstBadInOutput();
}
