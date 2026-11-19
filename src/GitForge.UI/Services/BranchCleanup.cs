using GitForge.Core.Models;

namespace GitForge.UI.Services;

/// <summary>
/// A stale branch chosen for deletion, as it was checked: it is deleted only if it still points at <see cref="TipSha"/>.
/// When another worktree has it checked out, that worktree is removed first.
/// </summary>
public sealed record BranchCleanupItem(string Branch, string TipSha, WorktreeInfo? Worktree);

public static class BranchCleanup
{
    private static readonly string[] Trunks = ["main", "master", "develop", "trunk"];

    /// <summary>
    /// Branches to judge stale branches against, best first: the remote's default branch, the usual trunk names (remote
    /// copies before local ones), then the current branch. Pull requests usually merge into the first one.
    /// </summary>
    public static IReadOnlyList<string> BaseCandidates(RepositorySnapshot snapshot, string? remoteDefault, string? currentBranch)
    {
        var list = new List<string>();
        void Add(string? name)
        {
            if (!string.IsNullOrEmpty(name) && !list.Contains(name, StringComparer.Ordinal)) list.Add(name);
        }

        if (snapshot.RemoteBranches.Any(r => r.Name == remoteDefault)) Add(remoteDefault);
        foreach (var trunk in Trunks)
        {
            foreach (var r in snapshot.RemoteBranches.Where(r => r.RemoteName is { } remote && r.Name == $"{remote}/{trunk}").OrderBy(r => r.RemoteName == "origin" ? 0 : 1))
            {
                Add(r.Name);
            }

            if (snapshot.LocalBranches.Any(b => b.Name == trunk)) Add(trunk);
        }

        Add(currentBranch);
        return list;
    }

    /// <summary>The branch a remote's HEAD points to ("origin/main"), from <c>git symbolic-ref refs/remotes/&lt;remote&gt;/HEAD</c> output.</summary>
    public static string? RemoteDefault(string symbolicRefOutput)
    {
        const string prefix = "refs/remotes/";
        var full = symbolicRefOutput.Trim();
        return full.StartsWith(prefix, StringComparison.Ordinal) && full.Length > prefix.Length ? full[prefix.Length..] : null;
    }
}
