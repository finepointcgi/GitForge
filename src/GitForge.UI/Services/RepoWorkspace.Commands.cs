using GitForge.Core.Abstractions;
using GitForge.Core.Models;
using MudBlazor;

namespace GitForge.UI.Services;

/// <summary>A file to open in the external diff tool: working copy (unstaged or <see cref="Staged"/>), one commit, or two revisions.</summary>
public sealed record DiffToolTarget(string Path, string? OldPath = null, bool Staged = false, string? CommitSha = null, string? From = null, string? To = null);

/// <summary>
/// The file a right-click menu acts on: a working-copy change (<see cref="Area"/> set), a file in a commit
/// (<see cref="CommitSha"/>), or a file that differs between two revisions (<see cref="From"/>, and <see cref="To"/>, null = working tree).
/// </summary>
public sealed record FileMenuTarget(string Path, string? OldPath, FileChangeKind Kind, StagingArea? Area = null, string? CommitSha = null, string? From = null, string? To = null)
{
    public static FileMenuTarget For(FileStatusEntry f) => new(f.Path, f.OldPath, f.Kind, f.Area);

    public static FileMenuTarget ForCommit(string sha, FileChange c) => new(c.Path, c.OldPath, c.Kind, CommitSha: sha);

    public static FileMenuTarget ForCompare(string from, string? to, FileChange c) => new(c.Path, c.OldPath, c.Kind, From: from, To: to);
}

/// <summary>Submodule safety, worktrees, bisect, patches and bundles, external tools, and file actions.</summary>
public sealed partial class RepoWorkspace
{
    // ---------- Push with the submodule check ----------

    /// <summary>
    /// Pushes with <c>--recurse-submodules=check</c>. When the pushed commits point to submodule commits that are on no
    /// remote (teammates could not check them out), asks whether to push those submodules first or push anyway.
    /// </summary>
    public async Task PushCheckedAsync(PushRequest request, string label, string success)
    {
        IReadOnlyList<string>? missing = null;
        var ok = await RunAsync(label, async (r, p) =>
        {
            try
            {
                await r.Remotes.PushAsync(request, p);
            }
            catch (GitCommandException ex) when (request.Submodules == SubmodulePushMode.Check && GitOutput.SubmodulesMissingOnRemote(ex.GitMessage) is { Count: > 0 } paths)
            {
                missing = paths;
            }
        });
        if (!ok) return;
        if (missing is null)
        {
            _snackbar.Add(success, Severity.Success);
            return;
        }

        var one = missing.Count == 1;
        var choice = await _dialogs.ShowMessageBoxAsync(
            "Submodule commits aren't pushed",
            $"This push points to commits in {string.Join(", ", missing)} that aren't on {(one ? "its" : "their")} remote yet. " +
            $"Teammates who pull would get a submodule commit they can't download. Push {(one ? "the submodule" : "those submodules")} first?",
            yesText: "Push submodules too", noText: "Push without them", cancelText: "Cancel");
        if (choice is null) return;
        if (choice == false)
        {
            await RunAsync(label, (r, p) => r.Remotes.PushAsync(request with { Submodules = SubmodulePushMode.None }, p), success);
            return;
        }

        // Push the submodule branch each recorded commit is on, then the superproject (checked again).
        await RunAsync($"Pushing {string.Join(", ", missing)}, then {request.LocalBranch}", async (r, p) =>
        {
            foreach (var path in missing)
            {
                var pushed = await r.Submodules.PushRecordedCommitAsync(path, request.LocalBranch, p);
                Info($"Pushed {path} to {pushed}", Severity.Success);
            }

            await r.Remotes.PushAsync(request, p);
        }, success);
    }

    // ---------- Submodules ----------

    public Task AddSubmoduleAsync(string url, string path, string? branch) =>
        RunAsync($"Adding submodule {path}", (r, p) => r.Submodules.AddAsync(url, path, branch, p),
            $"Added {path}. It and .gitmodules are staged; commit to record it.");

    public Task RemoveSubmoduleAsync(string path) =>
        RunAsync($"Removing submodule {path}", (r, _) => r.Submodules.RemoveAsync(path),
            $"Removed {path}. The removal is staged; commit to record it.");

    /// <summary>Moves submodules (all when <paramref name="paths"/> is null) to the newest commit on their remote branch; ones with local changes are skipped.</summary>
    public Task UpdateSubmodulesToRemoteAsync(IReadOnlyCollection<string>? paths = null)
    {
        var all = Snapshot?.Submodules ?? [];
        var wanted = (paths ?? all.Where(m => m.HeadSha is not null).Select(m => m.Path).ToList()).ToList();
        var dirty = all.Where(m => wanted.Contains(m.Path) && m.State.HasFlag(SubmoduleState.WorkDirDirty)).Select(m => m.Path).ToList();
        var targets = wanted.Except(dirty).ToList();
        if (dirty.Count > 0)
        {
            _snackbar.Add($"Skipped {string.Join(", ", dirty)}: commit or stash the changes inside {(dirty.Count == 1 ? "it" : "them")} first.", Severity.Warning, o => o.VisibleStateDuration = 10000);
        }

        if (targets.Count == 0) return Task.CompletedTask;
        var what = targets.Count == 1 ? targets[0] : $"{targets.Count} submodules";
        return RunAsync($"Updating {what} to the newest remote commit", (r, p) => r.Submodules.UpdateToRemoteAsync(targets, p),
            $"Moved {what} to the newest commit on {(targets.Count == 1 ? "its" : "their")} remote branch. Stage and commit to record it.");
    }

    /// <summary>The submodule at <paramref name="path"/>, when that working-copy path is a submodule rather than a file.</summary>
    public SubmoduleInfo? SubmoduleAt(string path) => Snapshot?.Submodules.FirstOrDefault(m => m.Path == path);

    // ---------- Worktrees ----------

    public IReadOnlyList<WorktreeInfo> Worktrees => _active?.Worktrees ?? [];

    /// <summary>Another worktree with <paramref name="branch"/> checked out (git won't check it out twice), or null.</summary>
    public WorktreeInfo? WorktreeHolding(string branch) => Worktrees.FirstOrDefault(w => !w.IsCurrent && w.Branch == branch);

    private async Task RefreshWorktreesAsync(RepoSession session)
    {
        try
        {
            var list = MayHaveWorktrees(session.Path) ? await session.Repo.Worktrees.ListAsync() : [];
            if (list.Count < 2) list = [];
            if (list.SequenceEqual(session.Worktrees)) return;
            session.Worktrees = list;
            if (session == _active) Notify();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            LogRefreshFailed(_log, ex);
        }
    }

    /// <summary>Cheap check before running <c>git worktree list</c> on every refresh: linked worktrees leave a "worktrees" folder.</summary>
    private static bool MayHaveWorktrees(string workDir)
    {
        var dotGit = Path.Combine(workDir, ".git");
        if (Directory.Exists(dotGit)) return Directory.Exists(Path.Combine(dotGit, "worktrees"));
        if (!File.Exists(dotGit)) return false;

        // A linked worktree's (or submodule's) .git file: "gitdir: <path>".
        var line = File.ReadLines(dotGit).FirstOrDefault() ?? string.Empty;
        if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return false;
        var gitDir = Path.GetFullPath(Path.Combine(workDir, line[7..].Trim()));
        return gitDir.Replace('\\', '/').Contains("/worktrees/", StringComparison.Ordinal) || Directory.Exists(Path.Combine(gitDir, "worktrees"));
    }

    public async Task AddWorktreeAsync(string path, string branch, string? newBranch, bool open)
    {
        var name = newBranch ?? branch;
        if (await RunAsync($"Creating a worktree for {name}", (r, _) => r.Worktrees.AddAsync(path, branch, newBranch), $"{name} is checked out in {path}") && open)
        {
            await OpenAsync(path);
        }
    }

    public async Task RemoveWorktreeAsync(WorktreeInfo worktree, bool force)
    {
        // Its tab holds the folder open; close it first.
        if (_sessions.FirstOrDefault(s => GitOutput.SamePath(s.Path, worktree.Path)) is { } tab && tab != _active) await CloseTabAsync(tab);
        var label = $"Removing worktree {worktree.Name}";
        var success = $"Removed worktree {worktree.Name}";
        var hasSubmodules = false;
        if (!await RunAsync(label, async (r, _) =>
            {
                try
                {
                    await r.Worktrees.RemoveAsync(worktree.Path, force);
                }
                catch (GitCommandException ex) when (!force && ex.GitMessage.Contains("containing submodules", StringComparison.Ordinal))
                {
                    hasSubmodules = true;
                }
            }))
        {
            return;
        }

        if (!hasSubmodules)
        {
            _snackbar.Add(success, Severity.Success);
            await ForgetRecentAsync(worktree.Path);
            return;
        }

        var confirm = await _dialogs.ShowMessageBoxAsync("Remove worktree",
            $"Git won't remove a worktree that contains submodules. GitForge can delete the folder {worktree.Path} and then forget the worktree, as git recommends. " +
            $"Anything uncommitted in that folder is lost; {(worktree.Branch is { } b ? $"the branch {b} and its" : "its")} commits stay.",
            yesText: "Delete the folder", cancelText: "Cancel");
        if (confirm == true && await RunAsync(label, (r, _) => r.Worktrees.RemoveAsync(worktree.Path, force: true), success)) await ForgetRecentAsync(worktree.Path);
    }

    /// <summary>Drops a deleted folder from the recent repositories list.</summary>
    private Task ForgetRecentAsync(string path) =>
        _settings.UpdateAsync(s => s with { RecentRepositories = s.RecentRepositories.Where(r => !GitOutput.SamePath(r, path)).ToList() });

    public Task PruneWorktreesAsync() => RunAsync("Pruning worktrees", (r, _) => r.Worktrees.PruneAsync(), "Forgot worktrees whose folders were deleted");

    // ---------- Bisect ----------

    public BisectStatus Bisect => _active?.Bisect ?? BisectStatus.None;
    public string? BisectMessage => _active?.BisectMessage;
    public bool IsBisecting => Snapshot?.Operation == RepositoryOperation.Bisect;

    /// <summary>Starts a bisect by marking <paramref name="sha"/> good or bad. Needs a clean working tree: each step checks out a commit.</summary>
    public Task StartBisectAsync(string sha, BisectMark mark)
    {
        if (!Status.IsClean)
        {
            Info("Commit or stash your changes before bisecting: each step checks out a different commit.", Severity.Warning);
            return Task.CompletedTask;
        }

        return BisectStepAsync($"Starting bisect ({mark.ToString().ToLowerInvariant()}: {Short(sha)})", r => r.History.StartBisectAsync(sha, mark));
    }

    /// <summary>Marks a commit (null = the one checked out for testing) good, bad, or skipped; git then checks out the next one.</summary>
    public Task MarkBisectAsync(BisectMark mark, string? sha = null) =>
        BisectStepAsync($"Marking {(sha is null ? "this commit" : Short(sha))} {mark.ToString().ToLowerInvariant()}", r => r.History.MarkBisectAsync(mark, sha));

    public async Task ResetBisectAsync()
    {
        var session = _active;
        if (await RunAsync("Ending bisect", (r, _) => r.History.ResetBisectAsync(), "Bisect ended. You're back where you started.") && session is not null)
        {
            session.BisectMessage = null;
        }
    }

    private async Task BisectStepAsync(string label, Func<IGitRepository, Task<GitResult>> step)
    {
        var session = _active;
        if (session is null) return;
        string? output = null;
        if (!await RunAsync(label, async (r, _) =>
            {
                var result = await step(r);
                output = $"{result.StandardOutput}\n{result.StandardError}";
            }) || output is null)
        {
            return;
        }

        // git names the first bad commit in its output; the refreshed bisect state finds it too, whichever stream it used.
        if ((GitOutput.FirstBadCommit(output) ?? session.Bisect.FirstBad) is { } bad)
        {
            session.BisectMessage = null;
            SelectCommit(bad, reveal: true);
            Info($"Found it: {Short(bad)} is the first bad commit. End the bisect to go back to your branch.", Severity.Success);
            return;
        }

        session.BisectMessage = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (session.Snapshot?.HeadSha is { } head && session.Bisect is { Bad: not null, Good.Count: > 0 }) SelectCommit(head, reveal: true);
        Notify();
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    // ---------- Patches and bundles ----------

    /// <summary>Saves commits (oldest first) as numbered .patch files. Merge commits are skipped, as git format-patch does.</summary>
    public Task SaveCommitsAsPatchesAsync(IReadOnlyList<CommitInfo> commitsOldestFirst, string directory)
    {
        var merges = commitsOldestFirst.Count(c => c.IsMerge);
        var shas = commitsOldestFirst.Where(c => !c.IsMerge).Select(c => c.Sha).ToList();
        if (shas.Count == 0)
        {
            Info("Merge commits can't be saved as patches. Select the commits that were merged instead.", Severity.Warning);
            return Task.CompletedTask;
        }

        return RunAsync($"Saving {Plural(shas.Count, "patch", "patches")}", async (r, _) =>
        {
            var files = await r.Transfer.SaveCommitsAsPatchesAsync(shas, directory);
            Info($"Saved {Plural(files.Count, "patch", "patches")} to {directory}{(merges > 0 ? $" (skipped {Plural(merges, "merge commit", "merge commits")})" : null)}", Severity.Success);
        });
    }

    public Task SaveUncommittedPatchAsync(string file) =>
        RunAsync("Creating a patch", async (r, _) =>
        {
            if (await r.Transfer.SaveUncommittedPatchAsync(file)) Info($"Saved your uncommitted changes to {file}", Severity.Success);
            else Info("There are no uncommitted changes to save.", Severity.Warning);
        });

    /// <summary>Applies a .patch file: commits from git format-patch keep their author and message; a plain diff changes files.</summary>
    public Task ApplyPatchAsync(string file) =>
        RunAsync($"Applying {Path.GetFileName(file)}", async (r, _) =>
        {
            var result = await r.Transfer.ApplyPatchAsync(file);
            if (result.Succeeded)
            {
                Info($"Applied {Path.GetFileName(file)}", Severity.Success);
                return;
            }

            var message = string.Join(' ', (result.StandardError + "\n" + result.StandardOutput).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(4));
            _snackbar.Add($"{Path.GetFileName(file)} didn't apply cleanly: {message} Resolve any conflicts as you would for a merge, or abort.", Severity.Warning, o => o.VisibleStateDuration = 15000);
        });

    public Task CreateBundleAsync(string file, IReadOnlyList<string> refs, string? basis) =>
        RunAsync("Creating a bundle", async (r, _) =>
        {
            var heads = await r.Transfer.CreateBundleAsync(file, refs, basis);
            Info($"Saved {Plural(heads.Count, "branch or tag", "branches and tags")} to {file}{(basis is null ? null : $" (commits after {basis})")}", Severity.Success);
        });

    public Task FetchBundleAsync(string file, string remoteName) =>
        RunAsync($"Fetching {Path.GetFileName(file)}", (r, p) => r.Transfer.FetchBundleAsync(file, remoteName, p),
            $"Fetched {Path.GetFileName(file)}. Its branches are under {remoteName}/ in the Remote list, ready to check out or merge.");

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // ---------- Files ----------

    /// <summary>Appends <paramref name="pattern"/> to the repository's root .gitignore (created if needed), keeping its line endings.</summary>
    public Task AddToGitignoreAsync(string pattern) =>
        RunAsync($"Ignoring {pattern}", async (r, _) =>
        {
            var file = Path.Combine(r.WorkingDirectory, ".gitignore");
            var text = File.Exists(file) ? await File.ReadAllTextAsync(file) : string.Empty;
            if (text.Split('\n').Any(l => l.Trim() == pattern))
            {
                Info($"{pattern} is already in .gitignore");
                return;
            }

            var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var prefix = text.Length > 0 && !text.EndsWith('\n') ? eol : string.Empty;
            await File.AppendAllTextAsync(file, prefix + pattern + eol, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Info($"Added {pattern} to .gitignore. Commit .gitignore to share it.", Severity.Success);
        });

    public Task StopTrackingAsync(string path) =>
        RunAsync($"Untracking {path}", (r, _) => r.Index.StopTrackingAsync([path]),
            $"{path} is no longer tracked; the file stays on disk. Commit to record it, and add it to .gitignore to keep it out.");

    public Task RestoreFileAsync(string revision, string path) =>
        RunAsync($"Restoring {path}", (r, _) => r.Index.RestoreFromAsync(revision, path), $"Restored {path} as of {Short(revision)} (staged)");

    /// <summary>Opens a file in the external diff tool (<c>git difftool</c>). Runs in the background; GitForge stays usable meanwhile.</summary>
    public async Task OpenDiffToolAsync(DiffToolTarget target)
    {
        if (Repo is not { } repo) return;
        var tool = Settings.ExternalDiffTool;
        if (string.IsNullOrWhiteSpace(tool))
        {
            // git difftool falls back to merge.tool; with neither set it would guess (possibly a console tool that never shows).
            var configured = (await repo.QueryAsync(["config", "--get", "diff.tool"])).StandardOutput.Trim()
                + (await repo.QueryAsync(["config", "--get", "merge.tool"])).StandardOutput.Trim();
            if (configured.Length == 0)
            {
                Info("Choose a diff tool in Settings › External tools (for example bc, meld, kdiff3, or vscode).", Severity.Warning);
                return;
            }
        }

        var args = new List<string> { "--literal-pathspecs", "difftool", "--no-prompt", "-M" };
        if (!string.IsNullOrWhiteSpace(tool)) args.Add($"--tool={tool.Trim()}");
        if (target.CommitSha is { } sha)
        {
            var parent = await repo.QueryAsync(["rev-parse", "--verify", "--quiet", sha + "^"]);
            // A root commit has no parent: compare with the empty tree (hashed for this repository's object format).
            var from = parent.Succeeded ? parent.StandardOutput.Trim() : (await repo.QueryAsync(["hash-object", "-t", "tree", "/dev/null"])).StandardOutput.Trim();
            args.AddRange([from, sha]);
        }
        else if (target.From is { } a)
        {
            args.Add(a);
            if (target.To is { } b) args.Add(b);
        }
        else if (target.Staged)
        {
            args.Add("--cached");
        }

        args.Add("--");
        if (target.OldPath is { } old) args.Add(old);
        args.Add(target.Path);
        Info($"Opening {Path.GetFileName(target.Path)} in {(string.IsNullOrWhiteSpace(tool) ? "your diff tool" : tool)}…");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await repo.QueryAsync(args);
                if (!result.Succeeded) Error("Diff tool", new GitCommandException("difftool", result.ExitCode, (result.StandardError + result.StandardOutput).Trim()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error("Diff tool", ex);
            }
        });
    }

    // ---------- Explorer, terminal, editor ----------

    private string? FullPath(string? relative) =>
        Repo is not { } repo ? null : relative is null ? repo.WorkingDirectory : Path.GetFullPath(Path.Combine(repo.WorkingDirectory, relative));

    public void RevealInExplorer(string? relative = null)
    {
        if (FullPath(relative) is { } path) _shell.RevealInFileManager(path);
    }

    public void OpenTerminal(string? folder = null)
    {
        var path = folder ?? FullPath(null);
        if (path is null) return;
        if (!_shell.OpenTerminal(path, Settings.TerminalCommand, _cli.GitPath))
        {
            Info(string.IsNullOrWhiteSpace(Settings.TerminalCommand)
                ? "Couldn't open a terminal. Set one in Settings › External tools."
                : "Couldn't start the terminal set in Settings › External tools.", Severity.Warning);
        }
    }

    public void OpenInEditor(string? relative = null)
    {
        if (FullPath(relative) is not { } path) return;
        if (!_shell.OpenInEditor(path, Settings.EditorCommand))
        {
            Info(string.IsNullOrWhiteSpace(Settings.EditorCommand)
                ? "Visual Studio Code wasn't found. Set your editor in Settings › External tools."
                : "Couldn't start the editor set in Settings › External tools. Use the program's .exe (a .cmd or .bat can't be used).", Severity.Warning);
        }
    }

    /// <summary>Opens a working-copy file with its default app; programs and scripts open in the editor instead, never run.</summary>
    public void OpenFile(string relative)
    {
        if (FullPath(relative) is not { } path) return;
        if (!File.Exists(path))
        {
            Info($"{relative} isn't in the working copy.", Severity.Warning);
            return;
        }

        if (ExternalTools.IsRunnable(path))
        {
            Info($"{Path.GetFileName(path)} is a program or script, so GitForge opens it in your editor instead of running it.");
            OpenInEditor(relative);
            return;
        }

        _shell.OpenFile(path);
    }
}
