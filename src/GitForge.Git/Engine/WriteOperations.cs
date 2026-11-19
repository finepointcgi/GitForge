using GitForge.Core.Abstractions;
using GitForge.Core.Diff;
using GitForge.Core.Models;
using GitForge.Core.Rebase;
using GitForge.Core.Validation;

namespace GitForge.Git.Engine;

/// <summary>Index, commit, and conflict operations (git CLI: honors hooks, signing, LFS filters).</summary>
internal sealed class IndexOperations(RepoContext ctx) : IIndexOperations
{
    public Task StageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        paths.Count == 0 ? Task.CompletedTask :
        ctx.WriteAsync("stage", ["--literal-pathspecs", "add", "-A", .. RepoContext.PathspecFromStdin], cancellationToken, RepoContext.PathspecPayload(paths));

    public Task StageAllAsync(CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("stage all", ["add", "-A"], cancellationToken);

    public async Task UnstageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        if (await IsUnbornAsync(cancellationToken).ConfigureAwait(false))
        {
            await ctx.WriteAsync("unstage", ["--literal-pathspecs", "rm", "--cached", "-r", "-q", "--", .. paths], cancellationToken).ConfigureAwait(false);
            return;
        }

        await ctx.WriteAsync("unstage", ["--literal-pathspecs", "restore", "--staged", .. RepoContext.PathspecFromStdin], cancellationToken, RepoContext.PathspecPayload(paths)).ConfigureAwait(false);
    }

    public async Task UnstageAllAsync(CancellationToken cancellationToken = default)
    {
        if (await IsUnbornAsync(cancellationToken).ConfigureAwait(false))
        {
            await ctx.WriteAsync("unstage all", ["rm", "--cached", "-r", "-q", "--", "."], cancellationToken).ConfigureAwait(false);
            return;
        }

        await ctx.WriteAsync("unstage all", ["reset", "-q"], cancellationToken).ConfigureAwait(false);
    }

    public async Task DiscardAsync(IReadOnlyCollection<FileStatusEntry> files, CancellationToken cancellationToken = default)
    {
        var untracked = files.Where(f => f.Kind == FileChangeKind.Untracked).Select(f => f.Path).ToList();
        var tracked = files.Where(f => f.Kind != FileChangeKind.Untracked).Select(f => f.Path).ToList();

        if (tracked.Count > 0)
        {
            await ctx.WriteAsync("discard", ["--literal-pathspecs", "restore", "--worktree", .. RepoContext.PathspecFromStdin], cancellationToken, RepoContext.PathspecPayload(tracked)).ConfigureAwait(false);
        }

        if (untracked.Count > 0)
        {
            // git clean only ever removes untracked files, so tracked content can never be lost here.
            await ctx.WriteAsync("remove untracked", ["--literal-pathspecs", "clean", "-f", "-q", "--", .. untracked], cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ApplySelectionAsync(FileDiff diff, IReadOnlySet<LineRef> selection, PatchAction action, CancellationToken cancellationToken = default)
    {
        // Unstaging every line of a newly added file means "not added": remove the entry instead of staging an empty file.
        if (diff.IsNewFile && action == PatchAction.Unstage && PatchBuilder.SelectsAllChanges(diff, selection))
        {
            await UnstageAsync([diff.Path], cancellationToken).ConfigureAwait(false);
            return;
        }

        var patch = PatchBuilder.Build(diff, selection, action);
        if (patch is null) return;
        await ctx.WriteAsync($"{action.ToString().ToLowerInvariant()} selection", ApplyArguments(diff, action), cancellationToken, patch).ConfigureAwait(false);
    }

    /// <summary><c>git apply</c> arguments for a patch built from <paramref name="diff"/>; the patch is read from standard input.</summary>
    internal static List<string> ApplyArguments(FileDiff diff, PatchAction action, bool checkOnly = false)
    {
        var args = new List<string> { "apply", "--whitespace=nowarn" };
        if (checkOnly) args.Add("--check");
        // git apply refuses context-free hunks (diffs produced with -U0) unless told to accept them.
        if (diff.Hunks.Any(h => h.Lines.All(l => l.IsChange))) args.Add("--unidiff-zero");
        if (action != PatchAction.Discard) args.Add("--cached");
        if (PatchBuilder.IsReverse(action)) args.Add("--reverse");
        args.Add("-");
        return args;
    }

    public async Task<string> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message) && !request.Amend) throw new ArgumentException("Commit message is required.", nameof(request));

        var args = new List<string> { "commit", "--cleanup=strip", "-F", "-" };
        if (request.Amend) args.Add("--amend");
        if (request.SignOff) args.Add("--signoff");
        if (request.AllowEmpty) args.Add("--allow-empty");
        await ctx.WriteAsync("commit", args, cancellationToken, request.Message).ConfigureAwait(false);

        var head = await ctx.RunAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        return head.EnsureSuccess("rev-parse").StandardOutput.Trim();
    }

    public Task<string?> GetHeadMessageAsync(CancellationToken cancellationToken = default) =>
        ctx.Lib.RunAsync(r => r.Info.IsHeadUnborn ? null : r.Head.Tip?.Message, cancellationToken);

    public async Task ResolveConflictAsync(string path, ConflictSide side, CancellationToken cancellationToken = default)
    {
        var flag = side == ConflictSide.Ours ? "--ours" : "--theirs";
        await ctx.WriteAsync("checkout side", ["--literal-pathspecs", "checkout", flag, "--", path], cancellationToken).ConfigureAwait(false);
        await MarkResolvedAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task MarkResolvedAsync(string path, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("mark resolved", ["--literal-pathspecs", "add", "--", path], cancellationToken);

    public Task<GitResult> LaunchMergeToolAsync(string path, string? tool, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "--literal-pathspecs", "mergetool", "--no-prompt" };
        if (!string.IsNullOrWhiteSpace(tool)) args.Add($"--tool={tool}");
        args.AddRange(["--", path]);
        return ctx.TryWriteAsync(args, cancellationToken);
    }

    public Task StopTrackingAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        paths.Count == 0 ? Task.CompletedTask :
        ctx.WriteAsync("stop tracking", ["--literal-pathspecs", "rm", "--cached", "-r", "-q", .. RepoContext.PathspecFromStdin], cancellationToken, RepoContext.PathspecPayload(paths));

    public Task RestoreFromAsync(string revision, string path, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("restore file", ["--literal-pathspecs", "restore", $"--source={BranchOperations.Guard(revision)}", "--staged", "--worktree", "--", path], cancellationToken);

    private Task<bool> IsUnbornAsync(CancellationToken ct) => ctx.Lib.RunAsync(r => r.Info.IsHeadUnborn, ct);

    public async Task<ConflictSides> GetConflictSidesAsync(string path, CancellationToken cancellationToken = default)
    {
        var (b, bBin) = await ReadStageAsync(1, path, cancellationToken).ConfigureAwait(false);
        var (o, oBin) = await ReadStageAsync(2, path, cancellationToken).ConfigureAwait(false);
        var (t, tBin) = await ReadStageAsync(3, path, cancellationToken).ConfigureAwait(false);
        return new ConflictSides(b, o, t, bBin || oBin || tBin);
    }

    public async Task<string?> GetMergeTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var sides = await GetConflictSidesAsync(path, cancellationToken).ConfigureAwait(false);
        if (sides.IsBinary || sides.Ours is null || sides.Theirs is null) return null;

        var dir = Directory.CreateTempSubdirectory("gitforge-merge-");
        try
        {
            var ours = Path.Combine(dir.FullName, "mine");
            var @base = Path.Combine(dir.FullName, "base");
            var theirs = Path.Combine(dir.FullName, "theirs");
            var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(ours, sides.Ours, utf8, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(@base, sides.Base ?? string.Empty, utf8, cancellationToken).ConfigureAwait(false); // add/add: empty base
            await File.WriteAllTextAsync(theirs, sides.Theirs, utf8, cancellationToken).ConfigureAwait(false);

            // Exit code = number of conflicts (0..127); negative values (255) are errors.
            var result = await ctx.RunAsync(
                ["merge-file", "-p", "--diff3", "-L", "mine", "-L", "base", "-L", "theirs", ours, @base, theirs],
                cancellationToken, okCodes: Enumerable.Range(0, 128).ToArray()).ConfigureAwait(false);
            return result.EnsureSuccess("merge-file").StandardOutput;
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public async Task SaveResolutionAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var full = SafeWorkPath(path);

        // Index stages hold normalized (usually LF) text; keep the working tree's CRLF convention if it had one.
        if (File.Exists(full) && !content.Contains("\r\n", StringComparison.Ordinal)
            && (await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false)).Contains("\r\n", StringComparison.Ordinal))
        {
            content = content.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(full, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        await MarkResolvedAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task ResolveDeletionAsync(string path, bool keepFile, CancellationToken cancellationToken = default) =>
        keepFile
            ? MarkResolvedAsync(path, cancellationToken)
            : ctx.WriteAsync("delete file", ["--literal-pathspecs", "rm", "-q", "--", path], cancellationToken);

    /// <summary>Reads index stage N (1 = base, 2 = ours, 3 = theirs). Null when the stage does not exist.</summary>
    private async Task<(string? Text, bool Binary)> ReadStageAsync(int stage, string path, CancellationToken ct)
    {
        var r = await ctx.RunAsync(["show", $":{stage}:{path}"], ct).ConfigureAwait(false);
        if (!r.Succeeded) return (null, false);
        var text = r.StandardOutput;
        var binary = text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0') || text.Contains('�', StringComparison.Ordinal);
        return (binary ? null : text, binary);
    }

    private string SafeWorkPath(string relative)
    {
        var root = Path.GetFullPath(ctx.WorkDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Path escapes the repository: {relative}");
        return full;
    }
}

internal sealed class BranchOperations(RepoContext ctx) : IBranchOperations
{
    public Task CheckoutAsync(string branchOrCommit, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("checkout", ["checkout", "--end-of-options", Guard(branchOrCommit), "--"], cancellationToken);

    public Task CheckoutRemoteBranchAsync(string remoteBranch, string localName, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("checkout", ["checkout", "-b", Valid(localName), "--track", Guard(remoteBranch), "--"], cancellationToken);

    public Task CreateAsync(string name, string startPoint, bool checkout, CancellationToken cancellationToken = default) =>
        checkout
            ? ctx.WriteAsync("create branch", ["checkout", "-b", Valid(name), Guard(startPoint), "--"], cancellationToken)
            : ctx.WriteAsync("create branch", ["branch", Valid(name), Guard(startPoint)], cancellationToken);

    public Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("rename branch", ["branch", "-m", Guard(oldName), Valid(newName)], cancellationToken);

    public Task DeleteAsync(string name, bool force, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("delete branch", ["branch", force ? "-D" : "-d", Guard(name)], cancellationToken);

    public Task DeleteRemoteAsync(string remote, string branch, IProgress<string>? progress, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("delete remote branch", ["push", "--progress", Guard(remote), "--delete", Guard(branch)], cancellationToken, progress: progress);

    public Task SetUpstreamAsync(string localBranch, string? upstream, CancellationToken cancellationToken = default) =>
        upstream is null
            ? ctx.WriteAsync("unset upstream", ["branch", "--unset-upstream", Guard(localBranch)], cancellationToken)
            : ctx.WriteAsync("set upstream", ["branch", $"--set-upstream-to={Guard(upstream)}", Guard(localBranch)], cancellationToken);

    public Task CreateTagAsync(string name, string target, string? message, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(message)
            ? ctx.WriteAsync("create tag", ["tag", Valid(name), Guard(target)], cancellationToken)
            : ctx.WriteAsync("create tag", ["tag", "-a", "-F", "-", Valid(name), Guard(target)], cancellationToken, message);

    public Task DeleteTagAsync(string name, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("delete tag", ["tag", "-d", Guard(name)], cancellationToken);

    internal static string Valid(string refName)
    {
        var error = RefNameValidator.Validate(refName);
        return error is null ? refName : throw new ArgumentException(error, nameof(refName));
    }

    /// <summary>Blocks option injection for existing ref names and revisions passed positionally.</summary>
    internal static string Guard(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-')) throw new ArgumentException($"Invalid ref or revision '{value}'.", nameof(value));
        return value;
    }
}

internal sealed class RemoteOperations(RepoContext ctx) : IRemoteOperations
{
    public Task FetchAsync(FetchRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "fetch", "--progress" };
        if (request.Prune) args.Add("--prune");
        if (request.Tags) args.Add("--tags");
        if (request.Remote is { } r) args.Add(BranchOperations.Guard(r));
        else if (request.AllRemotes) args.Add("--all");
        return ctx.WriteAsync("fetch", args, cancellationToken, progress: progress);
    }

    public Task PullAsync(PullChangesRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pull", "--progress", request.Rebase ? "--rebase" : "--no-rebase" };
        if (request.FastForwardOnly) args.Add("--ff-only");
        if (request.Remote is { } r)
        {
            args.Add(BranchOperations.Guard(r));
            if (request.Branch is { } b) args.Add(BranchOperations.Guard(b));
        }

        return ctx.WriteAsync("pull", args, cancellationToken, progress: progress);
    }

    public Task PushAsync(PushRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "push", "--progress" };
        if (request.SetUpstream) args.Add("--set-upstream");
        if (request.ForceWithLease) args.Add("--force-with-lease");
        if (request.IncludeTags) args.Add("--follow-tags");
        args.Add(request.Submodules switch
        {
            SubmodulePushMode.Check => "--recurse-submodules=check",
            SubmodulePushMode.OnDemand => "--recurse-submodules=on-demand",
            _ => "--recurse-submodules=no", // overrides push.recurseSubmodules when the user chose to push anyway
        });
        var local = BranchOperations.Guard(request.LocalBranch);
        var remoteBranch = BranchOperations.Guard(request.RemoteBranch ?? request.LocalBranch);
        args.AddRange([BranchOperations.Guard(request.Remote), $"refs/heads/{local}:refs/heads/{remoteBranch}"]);

        // English messages, so a refusal by the submodule check can be recognized (GitOutput.SubmodulesMissingOnRemote).
        return ctx.WriteAsync("push", args, cancellationToken, progress: progress, env: RepoContext.EnglishMessages);
    }

    public Task AddAsync(string name, string url, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("add remote", ["remote", "add", "--", BranchOperations.Valid(name), url], cancellationToken);

    public Task RemoveAsync(string name, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("remove remote", ["remote", "remove", BranchOperations.Guard(name)], cancellationToken);

    public Task PruneAsync(string remote, IProgress<string>? progress, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("prune", ["remote", "prune", BranchOperations.Guard(remote)], cancellationToken, progress: progress);
}

internal sealed class StashOperations(RepoContext ctx, IRepositoryReader reader) : IStashOperations
{
    public Task SaveAsync(StashRequest request, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "--literal-pathspecs", "stash", "push" };
        if (!string.IsNullOrWhiteSpace(request.Message)) args.AddRange(["-m", request.Message]);
        if (request.IncludeUntracked) args.Add("--include-untracked");
        if (request.KeepIndex) args.Add("--keep-index");
        if (request.Paths is { Count: > 0 } paths)
        {
            args.AddRange(RepoContext.PathspecFromStdin);
            return ctx.WriteAsync("stash", args, cancellationToken, RepoContext.PathspecPayload(paths));
        }

        return ctx.WriteAsync("stash", args, cancellationToken);
    }

    public async Task ApplyAsync(int index, bool pop, bool restoreIndex, CancellationToken cancellationToken = default)
    {
        var status = await reader.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Conflicted.Count > 0)
        {
            throw new InvalidOperationException("Resolve existing conflicts before applying a stash.");
        }

        // git stash apply refuses any file with uncommitted edits, even when the edits don't overlap: the usual state
        // after stashing some lines of a file and keeping the rest. Such stashes go in as one patch, all hunks or none.
        if (!restoreIndex && !status.IsClean)
        {
            var contents = await GetContentsAsync($"stash@{{{index}}}", cancellationToken).ConfigureAwait(false);
            var dirty = status.Staged.Concat(status.Unstaged).SelectMany(Paths).ToHashSet(StringComparer.Ordinal);
            var overlap = contents.Files.Where(f => !f.Untracked).SelectMany(f => Paths(f.Change)).Where(dirty.Contains).Distinct().ToList();
            if (overlap.Count > 0 && contents.UntrackedSha is null)
            {
                await ApplyAsPatchAsync(contents, overlap, cancellationToken).ConfigureAwait(false);
                if (pop) await DropAsync(index, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var args = new List<string> { "stash", pop ? "pop" : "apply" };
        if (restoreIndex) args.Add("--index");
        args.Add($"stash@{{{index}}}");
        await ctx.WriteAsync(pop ? "stash pop" : "stash apply", args, cancellationToken).ConfigureAwait(false);
    }

    public Task DropAsync(int index, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("stash drop", ["stash", "drop", $"stash@{{{index}}}"], cancellationToken);

    private static IEnumerable<string> Paths(FileStatusEntry f) => f.OldPath is null ? [f.Path] : [f.Path, f.OldPath];

    private static IEnumerable<string> Paths(FileChange c) => c.OldPath is null ? [c.Path] : [c.Path, c.OldPath];

    /// <summary>Applies the stash's changes to the working tree with <c>git apply</c>, which is atomic: on failure nothing changes.</summary>
    private async Task ApplyAsPatchAsync(StashContents stash, List<string> overlap, CancellationToken ct)
    {
        var diff = await ctx.RunAsync(["diff", "--binary", "--no-color", "--no-ext-diff", "--no-textconv", stash.BaseSha, stash.Sha, "--"], ct).ConfigureAwait(false);
        var patch = diff.EnsureSuccess("stash apply").StandardOutput;
        if (patch.Length == 0) return;

        var result = await ctx.TryWriteAsync(["apply", "--whitespace=nowarn", "-"], ct, patch).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var files = string.Join(", ", overlap.Take(3)) + (overlap.Count > 3 ? $", and {overlap.Count - 3} more" : string.Empty);
            throw new InvalidOperationException($"The stash changes the same lines as your uncommitted edits in {files}. Commit or stash those edits, then try again. Nothing was changed.");
        }
    }

    public async Task<IReadOnlyList<FileChange>> GetChangesAsync(int index, CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["stash", "show", "--name-status", "-M", "--include-untracked", $"stash@{{{index}}}"], cancellationToken).ConfigureAwait(false);
        return ParseNameStatus(r.EnsureSuccess("stash show").StandardOutput);
    }

    /// <summary>
    /// A stash commit's parents are the commit it was made on, the saved index, and (with untracked files) a root
    /// commit holding only the untracked files.
    /// </summary>
    public async Task<StashContents> GetContentsAsync(string stashSha, CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["rev-list", "--parents", "-n", "1", BranchOperations.Guard(stashSha), "--"], cancellationToken).ConfigureAwait(false);
        var shas = r.EnsureSuccess("rev-list").StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (shas.Length < 3) throw new InvalidOperationException($"{stashSha} is not a stash.");

        var files = (await reader.GetChangesBetweenAsync(shas[1], shas[0], cancellationToken).ConfigureAwait(false))
            .Select(c => new StashFile(c, Untracked: false))
            .ToList();
        var untrackedSha = shas.Length > 3 ? shas[3] : null;
        if (untrackedSha is not null)
        {
            var tree = await ctx.RunAsync(["ls-tree", "-r", "--name-only", "-z", untrackedSha], cancellationToken).ConfigureAwait(false);
            files.AddRange(tree.EnsureSuccess("ls-tree").StandardOutput
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => new StashFile(new FileChange(p, null, FileChangeKind.Added), Untracked: true)));
        }

        return new StashContents(shas[0], shas[1], untrackedSha, files);
    }

    public Task<FileDiff?> GetDiffAsync(StashContents stash, StashFile file, DiffOptions options, CancellationToken cancellationToken = default) =>
        file.Untracked && stash.UntrackedSha is { } untracked
            ? reader.GetCommitDiffAsync(untracked, file.Change, options, cancellationToken) // root commit: every file shows as added
            : reader.GetDiffBetweenAsync(stash.BaseSha, stash.Sha, file.Change, options, cancellationToken);

    /// <summary>
    /// Builds the stash the way <c>git stash push --patch</c> does: a temporary index starts at HEAD and receives the
    /// selected lines (the stash's working tree); the real index becomes the stash's index commit. The stash is stored
    /// only after a dry run proves the lines can be removed from the working tree, and is never signed (as with git stash).
    /// </summary>
    public async Task SaveSelectionAsync(FileDiff diff, IReadOnlySet<LineRef> selection, string? message, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (diff.IsNewFile) throw new InvalidOperationException($"'{diff.Path}' is a new file. Stash it as a whole with \"Include untracked files\".");
        var stage = PatchBuilder.Build(diff, selection, PatchAction.Stage) ?? throw new InvalidOperationException("Select at least one changed line to stash.");
        var discard = PatchBuilder.Build(diff, selection, PatchAction.Discard)!;

        var head = (await ctx.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], ct, okCodes: [0, 1, 128]).ConfigureAwait(false)).StandardOutput.Trim();
        if (head.Length < 40) throw new InvalidOperationException("Commit at least once before stashing.");

        var staged = await ctx.RunAsync(["--literal-pathspecs", "diff", "--cached", "--quiet", "--", diff.Path], ct, okCodes: [0, 1]).ConfigureAwait(false);
        if (staged.ExitCode == 1) throw new InvalidOperationException($"'{diff.Path}' has staged changes. Commit or unstage them before stashing lines.");

        (await ctx.RunAsync(IndexOperations.ApplyArguments(diff, PatchAction.Discard, checkOnly: true), ct, discard).ConfigureAwait(false)).EnsureSuccess("stash lines");

        var temp = Directory.CreateTempSubdirectory("gitforge-stash-");
        try
        {
            var tempIndex = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(temp.FullName, "index") };
            (await ctx.RunAsync(["read-tree", head], ct, env: tempIndex).ConfigureAwait(false)).EnsureSuccess("read-tree");
            (await ctx.RunAsync(IndexOperations.ApplyArguments(diff, PatchAction.Stage), ct, stage, env: tempIndex).ConfigureAwait(false)).EnsureSuccess("stash lines");
            var workTree = (await ctx.RunAsync(["write-tree"], ct, env: tempIndex).ConfigureAwait(false)).EnsureSuccess("write-tree").StandardOutput.Trim();
            var indexTree = (await ctx.RunAsync(["write-tree"], ct).ConfigureAwait(false)).EnsureSuccess("write-tree").StandardOutput.Trim();

            // Same messages as git stash: "WIP on <branch>: <sha> <subject>" or "On <branch>: <message>".
            var branch = (await ctx.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], ct, okCodes: [0, 1]).ConfigureAwait(false)).StandardOutput.Trim();
            if (branch.Length == 0) branch = "(no branch)";
            var subject = (await ctx.RunAsync(["log", "-1", "--format=%h %s", head, "--"], ct).ConfigureAwait(false)).EnsureSuccess("log").StandardOutput.Trim();
            var title = string.IsNullOrWhiteSpace(message) ? $"WIP on {branch}: {subject}" : $"On {branch}: {message.Trim()}";

            var indexCommit = await CommitTreeAsync(indexTree, [head], $"index on {branch}: {subject}", ct).ConfigureAwait(false);
            var stashCommit = await CommitTreeAsync(workTree, [head, indexCommit], title, ct).ConfigureAwait(false);
            (await ctx.RunAsync(["stash", "store", "-m", title, stashCommit], ct).ConfigureAwait(false)).EnsureSuccess("stash store");
        }
        finally
        {
            try
            {
                temp.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }

        await ctx.WriteAsync("stash lines", IndexOperations.ApplyArguments(diff, PatchAction.Discard), ct, discard).ConfigureAwait(false);
    }

    private async Task<string> CommitTreeAsync(string tree, IReadOnlyList<string> parents, string message, CancellationToken ct)
    {
        var args = new List<string> { "commit-tree", "--no-gpg-sign", "-F", "-" };
        foreach (var p in parents) args.AddRange(["-p", p]);
        args.Add(tree);
        return (await ctx.RunAsync(args, ct, message).ConfigureAwait(false)).EnsureSuccess("commit-tree").StandardOutput.Trim();
    }

    internal static IReadOnlyList<FileChange> ParseNameStatus(string output)
    {
        var list = new List<FileChange>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            var kind = parts[0][0] switch
            {
                'A' => FileChangeKind.Added,
                'D' => FileChangeKind.Deleted,
                'R' => FileChangeKind.Renamed,
                'C' => FileChangeKind.Copied,
                'T' => FileChangeKind.TypeChanged,
                'U' => FileChangeKind.Conflicted,
                _ => FileChangeKind.Modified,
            };
            list.Add(parts.Length >= 3 ? new FileChange(parts[2], parts[1], kind) : new FileChange(parts[1], null, kind));
        }

        return list;
    }
}

internal sealed class HistoryOperations(RepoContext ctx) : IHistoryOperations
{
    /// <summary>Accept default messages (merge, squash, continue) without opening an editor.</summary>
    private static readonly Dictionary<string, string> NoEditor = new() { ["GIT_EDITOR"] = "true" };

    public Task MergeAsync(string branch, bool noFastForward, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("merge", noFastForward
            ? ["merge", "--no-ff", "--no-edit", BranchOperations.Guard(branch)]
            : ["merge", "--no-edit", BranchOperations.Guard(branch)], cancellationToken);

    public Task RebaseOntoAsync(string upstream, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("rebase", ["rebase", BranchOperations.Guard(upstream)], cancellationToken, env: NoEditor);

    public async Task CherryPickAsync(IReadOnlyList<string> shas, CancellationToken cancellationToken = default)
    {
        var before = await HeadAsync(cancellationToken).ConfigureAwait(false);
        var result = await ctx.TryWriteAsync(["cherry-pick", .. shas.Select(BranchOperations.Guard)], cancellationToken, env: NoEditor).ConfigureAwait(false);
        if (result.Succeeded) return;

        // Refused before the first commit was picked (local changes in the way): git still leaves the sequence
        // "in progress". Clear it so a refusal changes nothing; a stop after some picks stays for continue or abort.
        if (shas.Count > 1 && Directory.Exists(Path.Combine(ctx.GitDir, "sequencer")) && !File.Exists(Path.Combine(ctx.GitDir, "CHERRY_PICK_HEAD"))
            && await HeadAsync(cancellationToken).ConfigureAwait(false) == before)
        {
            await ctx.TryWriteAsync(["cherry-pick", "--quit"], cancellationToken).ConfigureAwait(false);
        }

        result.EnsureSuccess("cherry-pick");
    }

    private async Task<string> HeadAsync(CancellationToken ct) =>
        (await ctx.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], ct, okCodes: [0, 1]).ConfigureAwait(false)).StandardOutput.Trim();

    public async Task RevertAsync(string sha, CancellationToken cancellationToken = default)
    {
        var isMerge = await ctx.Lib.RunAsync(r => LibGit2Sharp.RepositoryExtensions.Lookup<LibGit2Sharp.Commit>(r, sha)?.Parents.Count() > 1, cancellationToken).ConfigureAwait(false);
        await ctx.WriteAsync("revert", isMerge
            ? ["revert", "--no-edit", "-m", "1", BranchOperations.Guard(sha)]
            : ["revert", "--no-edit", BranchOperations.Guard(sha)], cancellationToken).ConfigureAwait(false);
    }

    public Task ResetAsync(string target, ResetMode mode, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("reset", ["reset", $"--{mode.ToString().ToLowerInvariant()}", BranchOperations.Guard(target), "--"], cancellationToken);

    public async Task<IReadOnlyList<RebaseTodoItem>> GetRebasePlanAsync(string baseSha, CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["log", "--reverse", "--no-merges", "--format=%H%x1f%s", $"{BranchOperations.Guard(baseSha)}..HEAD", "--"], cancellationToken).ConfigureAwait(false);
        return r.EnsureSuccess("log").StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\x1f'))
            .Where(p => p.Length == 2)
            .Select(p => new RebaseTodoItem(p[0], p[1]))
            .ToList();
    }

    public async Task<GitResult> RunInteractiveRebaseAsync(string baseSha, IReadOnlyList<RebaseTodoItem> plan, CancellationToken cancellationToken = default)
    {
        var workDir = Directory.CreateTempSubdirectory("gitforge-rebase-");
        try
        {
            var todo = RebaseTodoWriter.Write(plan, item =>
            {
                var file = Path.Combine(workDir.FullName, $"msg-{item.ShortSha}.txt");
                File.WriteAllText(file, item.NewMessage);
                return file;
            });
            var todoPath = Path.Combine(workDir.FullName, "git-rebase-todo");
            await File.WriteAllTextAsync(todoPath, todo, cancellationToken).ConfigureAwait(false);

            // git appends the todo path it wants edited; we overwrite it with the visual plan.
            var env = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp {RebaseTodoWriter.ShellQuote(todoPath)}",
                ["GIT_EDITOR"] = "true",
            };
            return await ctx.TryWriteAsync(["rebase", "-i", BranchOperations.Guard(baseSha)], cancellationToken, env: env).ConfigureAwait(false);
        }
        finally
        {
            // Reword message files are read by "exec" lines; keep them while a rebase is stopped mid-way.
            var inProgress = Directory.Exists(Path.Combine(ctx.GitDir, "rebase-merge"));
            if (!inProgress) TryDelete(workDir);
        }
    }

    public Task<GitResult> ContinueAsync(CancellationToken cancellationToken = default) => SequencerAsync("--continue", cancellationToken);

    public Task<GitResult> AbortAsync(CancellationToken cancellationToken = default) => SequencerAsync("--abort", cancellationToken);

    public Task<GitResult> SkipAsync(CancellationToken cancellationToken = default) => SequencerAsync("--skip", cancellationToken);

    public async Task<GitResult> StartBisectAsync(string revision, BisectMark mark, CancellationToken cancellationToken = default)
    {
        if (mark == BisectMark.Skip) throw new ArgumentException("Start a bisect by marking a commit good or bad.", nameof(mark));
        await ctx.WriteAsync("bisect start", ["bisect", "start"], cancellationToken, env: RepoContext.EnglishMessages).ConfigureAwait(false);
        return await MarkBisectAsync(mark, revision, cancellationToken).ConfigureAwait(false);
    }

    public Task<GitResult> MarkBisectAsync(BisectMark mark, string? revision = null, CancellationToken cancellationToken = default)
    {
        var verb = mark switch
        {
            BisectMark.Good => "good",
            BisectMark.Bad => "bad",
            _ => "skip",
        };
        List<string> args = revision is null ? ["bisect", verb] : ["bisect", verb, BranchOperations.Guard(revision)];
        return ctx.WriteAsync($"bisect {verb}", args, cancellationToken, env: RepoContext.EnglishMessages);
    }

    public Task ResetBisectAsync(CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("bisect reset", ["bisect", "reset"], cancellationToken);

    public async Task<BisectStatus> GetBisectStatusAsync(CancellationToken cancellationToken = default)
    {
        var logPath = Path.Combine(ctx.GitDir, "BISECT_LOG");
        if (!File.Exists(logPath)) return BisectStatus.None;

        var refs = await ctx.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)", "refs/bisect/"], cancellationToken).ConfigureAwait(false);
        var log = await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false);
        var status = GitOutput.ParseBisect(refs.EnsureSuccess("for-each-ref").StandardOutput, log, null);
        if (status.Bad is null || status.Good.Count == 0 || status.FirstBad is not null) return status;

        // Commits between the good ones and the bad one, not counting the bad one itself: what is left to test. When only
        // the bad commit remains, it is the first bad one (whatever git printed or logged).
        var count = await ctx.RunAsync(["rev-list", "--count", status.Bad, "--not", .. status.Good], cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(count.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n)) return status;
        return n <= 1 ? status with { FirstBad = status.Bad, Remaining = 0 } : status with { Remaining = n - 1 };
    }

    private async Task<GitResult> SequencerAsync(string verb, CancellationToken ct)
    {
        var op = await ctx.Lib.RunAsync(RepositoryReader.DetectOperation, ct).ConfigureAwait(false);
        var command = op switch
        {
            RepositoryOperation.Merge => "merge",
            RepositoryOperation.CherryPick => "cherry-pick",
            RepositoryOperation.Revert => "revert",
            RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive => "rebase",
            RepositoryOperation.ApplyMailbox => "am",
            _ => throw new InvalidOperationException("No merge, rebase, cherry-pick, or revert is in progress."),
        };
        if (command == "merge" && verb == "--skip") throw new InvalidOperationException("A merge cannot be skipped; abort it instead.");
        return await ctx.TryWriteAsync([command, verb], ct, env: NoEditor).ConfigureAwait(false);
    }

    private static void TryDelete(DirectoryInfo dir)
    {
        try
        {
            dir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will remove it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class SubmoduleOperations(RepoContext ctx) : ISubmoduleOperations
{
    public Task UpdateAsync(bool init, bool recursive, IProgress<string>? progress, CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(null, init, recursive, progress, cancellationToken);

    public Task UpdateAsync(IReadOnlyCollection<string> paths, bool init, bool recursive, IProgress<string>? progress, CancellationToken cancellationToken = default) =>
        paths.Count == 0 ? Task.CompletedTask : UpdateCoreAsync(paths, init, recursive, progress, cancellationToken);

    public Task SyncAsync(bool recursive, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("submodule sync", recursive ? ["submodule", "sync", "--recursive"] : ["submodule", "sync"], cancellationToken);

    public Task SyncAsync(IReadOnlyCollection<string> paths, bool recursive, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        var args = new List<string> { "--literal-pathspecs", "submodule", "sync" };
        if (recursive) args.Add("--recursive");
        args.Add("--");
        args.AddRange(paths);
        return ctx.WriteAsync("submodule sync", args, cancellationToken);
    }

    /// <summary>
    /// Recursive submodule state without <c>git submodule status</c>, whose per-submodule <c>git describe</c> calls take
    /// seconds on real projects. Compares each gitlink recorded in the index with the submodule's checked-out HEAD.
    /// Paths are relative to the superproject root, and each submodule precedes its nested submodules.
    /// </summary>
    public async Task<IReadOnlyList<SubmoduleStatusEntry>> GetRecursiveStatusAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<SubmoduleStatusEntry>();
        await CollectStatusAsync(string.Empty, 0, entries, cancellationToken).ConfigureAwait(false);
        return entries;
    }

    private async Task CollectStatusAsync(string dir, int depth, List<SubmoduleStatusEntry> into, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(ctx.WorkDir, dir, ".gitmodules"))) return;
        var listing = await ctx.RunAsync(In(dir, "ls-files", "--stage", "-z"), ct).ConfigureAwait(false);
        if (!listing.Succeeded) return;

        // Siblings are independent: inspect them in parallel, then keep index order.
        var subtrees = await Task.WhenAll(ParseGitlinks(listing.StandardOutput).Select(l => InspectAsync(dir, l, depth, ct))).ConfigureAwait(false);
        foreach (var subtree in subtrees) into.AddRange(subtree);
    }

    private async Task<List<SubmoduleStatusEntry>> InspectAsync(string dir, Gitlink link, int depth, CancellationToken ct)
    {
        var path = dir.Length == 0 ? link.Path : $"{dir}/{link.Path}";
        if (link.Conflicted) return [new SubmoduleStatusEntry(path, link.Sha, SubmoduleSyncState.Conflicted, depth, null)];

        // Without its own .git, "git -C <path>" would resolve to the superproject, so check first.
        var dotGit = Path.Combine(ctx.WorkDir, path, ".git");
        string[] head = [];
        if (File.Exists(dotGit) || Directory.Exists(dotGit))
        {
            var r = await ctx.RunAsync(In(path, "rev-parse", "HEAD", "--abbrev-ref", "HEAD"), ct, okCodes: [0, 128]).ConfigureAwait(false);
            if (r.ExitCode == 0) head = r.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        if (head.Length == 0 || head[0].Length < 40) return [new SubmoduleStatusEntry(path, link.Sha, SubmoduleSyncState.NotInitialized, depth, null)];

        var branch = head.Length > 1 && head[1] != "HEAD" ? head[1] : null;
        var state = string.Equals(head[0], link.Sha, StringComparison.OrdinalIgnoreCase) ? SubmoduleSyncState.Current : SubmoduleSyncState.OutOfSync;
        var entries = new List<SubmoduleStatusEntry> { new(path, head[0], state, depth, branch) };
        await CollectStatusAsync(path, depth + 1, entries, ct).ConfigureAwait(false);
        return entries;
    }

    private static string[] In(string dir, params string[] args) => dir.Length == 0 ? args : ["-C", dir, .. args];

    public async Task UpdateOneAsync(string path, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var (owner, inner) = SplitOwner(RepoContext.RelativePath(path));
        await ctx.WriteAsync("submodule sync", In(owner, "--literal-pathspecs", "submodule", "sync", "--recursive", "--", inner), cancellationToken).ConfigureAwait(false);
        await ctx.WriteAsync("submodule update", In(owner, "--literal-pathspecs", "submodule", "update", "--init", "--recursive", "--progress", "--", inner), cancellationToken, progress: progress).ConfigureAwait(false);
    }

    /// <summary>
    /// The innermost submodule that contains <paramref name="path"/> (its own repository; empty for the superproject) and
    /// the path relative to it. "libs/sub/nested" belongs to "libs/sub" when libs/sub has its own .git.
    /// </summary>
    internal (string Owner, string Inner) SplitOwner(string path)
    {
        var parts = path.Split('/');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            var owner = string.Join('/', parts[..i]);
            var dotGit = Path.Combine(ctx.WorkDir, owner, ".git");
            if (File.Exists(dotGit) || Directory.Exists(dotGit)) return (owner, string.Join('/', parts[i..]));
        }

        return (string.Empty, path);
    }

    public async Task UpdateToRemoteAsync(IReadOnlyCollection<string> paths, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        var list = paths.Select(RepoContext.RelativePath).ToList();
        await SyncAsync(list, recursive: false, cancellationToken).ConfigureAwait(false);
        await ctx.WriteAsync("submodule update --remote", ["--literal-pathspecs", "submodule", "update", "--init", "--remote", "--progress", "--", .. list], cancellationToken, progress: progress).ConfigureAwait(false);

        // Nested submodules follow the commits the new top-level commits record, not their own newest commits.
        foreach (var path in list.Where(p => File.Exists(Path.Combine(ctx.WorkDir, p, ".gitmodules"))))
        {
            await ctx.WriteAsync("submodule update", ["-C", path, "submodule", "update", "--init", "--recursive", "--progress"], cancellationToken, progress: progress).ConfigureAwait(false);
        }
    }

    public async Task<string> PushRecordedCommitAsync(string path, string superRevision, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var p = RepoContext.RelativePath(path);
        var recorded = (await ctx.RunAsync(["rev-parse", "--verify", "--quiet", $"{BranchOperations.Guard(superRevision)}:{p}"], ct, okCodes: [0, 1, 128]).ConfigureAwait(false)).StandardOutput.Trim();
        if (recorded.Length < 40) throw new InvalidOperationException($"{superRevision} doesn't record a commit for {p}.");

        var current = (await ctx.RunAsync(["-C", p, "symbolic-ref", "--quiet", "--short", "HEAD"], ct, okCodes: [0, 1, 128]).ConfigureAwait(false)).StandardOutput.Trim();
        var containing = (await ctx.RunAsync(["-C", p, "branch", "--contains", recorded, "--format=%(refname:short)"], ct).ConfigureAwait(false))
            .EnsureSuccess("branch --contains").StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(b => !b.StartsWith('(') && b != "HEAD")
            .ToList();
        var branch = containing.Contains(current) ? current : containing.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The commit {recorded[..8]} that this push records for {p} isn't on any branch in the submodule (it was made on a detached HEAD). " +
                $"Open {p} in GitForge, create a branch at that commit, and push again.");

        var upstreamRemote = (await ctx.RunAsync(["-C", p, "config", "--get", $"branch.{branch}.remote"], ct, okCodes: [0, 1]).ConfigureAwait(false)).StandardOutput.Trim();
        var upstreamRef = (await ctx.RunAsync(["-C", p, "config", "--get", $"branch.{branch}.merge"], ct, okCodes: [0, 1]).ConfigureAwait(false)).StandardOutput.Trim();
        var remotes = (await ctx.RunAsync(["-C", p, "remote"], ct).ConfigureAwait(false)).EnsureSuccess("remote").StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var hasUpstream = upstreamRemote.Length > 0 && upstreamRemote != "." && upstreamRef.StartsWith("refs/heads/", StringComparison.Ordinal);
        var remote = hasUpstream ? upstreamRemote
            : remotes.Contains("origin") ? "origin"
            : remotes.FirstOrDefault() ?? throw new InvalidOperationException($"{p} has no remote to push {branch} to.");
        var target = hasUpstream ? upstreamRef["refs/heads/".Length..] : branch;

        List<string> args = ["-C", p, "push", "--progress"];
        if (!hasUpstream) args.Add("--set-upstream");
        args.AddRange([BranchOperations.Guard(remote), $"refs/heads/{BranchOperations.Guard(branch)}:refs/heads/{BranchOperations.Guard(target)}"]);
        await ctx.WriteAsync($"push {p}", args, ct, progress: progress).ConfigureAwait(false);
        return $"{remote}/{target}";
    }

    public Task AddAsync(string url, string path, string? branch, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Enter the URL of the repository to add.", nameof(url));
        var args = new List<string> { "submodule", "add" };
        if (!string.IsNullOrWhiteSpace(branch)) args.AddRange(["-b", BranchOperations.Valid(branch.Trim())]);
        args.AddRange(["--", url.Trim(), RepoContext.RelativePath(path)]);
        return ctx.WriteAsync("submodule add", args, cancellationToken, progress: progress);
    }

    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        var p = RepoContext.RelativePath(path);
        await ctx.WriteAsync("submodule deinit", ["--literal-pathspecs", "submodule", "deinit", "-f", "--", p], cancellationToken).ConfigureAwait(false);
        await ctx.WriteAsync("remove submodule", ["--literal-pathspecs", "rm", "-f", "-q", "--", p], cancellationToken).ConfigureAwait(false);
    }

    internal readonly record struct Gitlink(string Path, string Sha, bool Conflicted);

    /// <summary>
    /// Picks submodule entries (mode 160000) from <c>git ls-files --stage -z</c>: <c>&lt;mode&gt; &lt;sha&gt; &lt;stage&gt;\t&lt;path&gt;\0</c>.
    /// A path with non-zero stages is in a merge conflict.
    /// </summary>
    internal static IReadOnlyList<Gitlink> ParseGitlinks(string output)
    {
        var links = new List<Gitlink>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!record.StartsWith("160000 ", StringComparison.Ordinal)) continue;
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            var meta = tab > 0 ? record[..tab].Split(' ') : [];
            if (meta.Length < 3) continue;
            var path = record[(tab + 1)..];
            var conflicted = meta[2] != "0";
            var existing = links.FindIndex(l => l.Path == path);
            if (existing >= 0) links[existing] = links[existing] with { Conflicted = true };
            else links.Add(new Gitlink(path, meta[1], conflicted));
        }

        return links;
    }

    private Task<GitResult> UpdateCoreAsync(IReadOnlyCollection<string>? paths, bool init, bool recursive, IProgress<string>? progress, CancellationToken ct)
    {
        var args = new List<string> { "--literal-pathspecs", "submodule", "update", "--progress" };
        if (init) args.Add("--init");
        if (recursive) args.Add("--recursive");
        if (paths is not null)
        {
            args.Add("--");
            args.AddRange(paths);
        }

        return ctx.WriteAsync("submodule update", args, ct, progress: progress);
    }
}
