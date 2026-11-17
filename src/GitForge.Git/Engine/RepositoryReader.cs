using System.Globalization;
using System.Runtime.CompilerServices;
using GitForge.Core.Abstractions;
using GitForge.Core.Diff;
using GitForge.Core.Hosting;
using GitForge.Core.Models;
using LibGit2Sharp;
using LgStatus = LibGit2Sharp.FileStatus;

namespace GitForge.Git.Engine;

/// <summary>Read paths: libgit2 for refs, status, and object lookups; git CLI for streamed history and apply-compatible diffs.</summary>
internal sealed class RepositoryReader(RepoContext ctx) : IRepositoryReader
{
    private const char FieldSep = '\x1f';
    private const char RecordSep = '\x1e';

    public Task<RepositorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        ctx.Lib.RunAsync(repo =>
        {
            var info = repo.Info;
            var detached = info.IsHeadDetached;
            var headSha = info.IsHeadUnborn ? null : repo.Head.Tip?.Sha;
            var local = new List<BranchHead>();
            var remote = new List<BranchHead>();

            foreach (var b in repo.Branches)
            {
                if (b.IsRemote)
                {
                    if (b.CanonicalName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                    remote.Add(new BranchHead(b.FriendlyName, b.CanonicalName, b.Tip?.Sha, true, false, b.RemoteName, null, null, null));
                    continue;
                }

                var upstream = b.TrackedBranch?.FriendlyName ?? ShortenUpstream(b.UpstreamBranchCanonicalName, b.RemoteName);
                var tracking = b.TrackedBranch?.Tip is not null ? b.TrackingDetails : null;
                local.Add(new BranchHead(
                    b.FriendlyName, b.CanonicalName, b.Tip?.Sha, false, b.IsCurrentRepositoryHead && !detached,
                    b.RemoteName, upstream, tracking?.AheadBy, tracking?.BehindBy));
            }

            var tags = repo.Tags
                .Select(t => new TagInfo(t.FriendlyName, (t.PeeledTarget ?? t.Target).Sha, t.IsAnnotated, t.Annotation?.Message?.Trim()))
                .ToList();

            var resolver = ctx.Options.SelfHostedResolver;
            var remotes = repo.Network.Remotes
                .Select(r => new RemoteInfo(r.Name, r.Url, r.PushUrl, RemoteUrlParser.Parse(r.Url, resolver)))
                .ToList();

            var stashes = repo.Stashes
                .Select((s, i) => new StashInfo(i, s.WorkTree.Sha, s.Message, s.WorkTree.Author.When))
                .ToList();

            var submodules = new List<SubmoduleInfo>();
            foreach (var sm in repo.Submodules)
            {
                var st = sm.RetrieveStatus();
                var state = SubmoduleState.None;
                if (st.HasFlag(SubmoduleStatus.WorkDirUninitialized)) state |= SubmoduleState.NotInitialized;
                if (st.HasFlag(SubmoduleStatus.WorkDirModified)) state |= SubmoduleState.OutOfSync;
                if (st.HasFlag(SubmoduleStatus.IndexModified)) state |= SubmoduleState.Modified;
                if ((st & (SubmoduleStatus.WorkDirFilesModified | SubmoduleStatus.WorkDirFilesIndexDirty | SubmoduleStatus.WorkDirFilesUntracked)) != 0)
                {
                    state |= SubmoduleState.WorkDirDirty;
                }

                submodules.Add(new SubmoduleInfo(sm.Name, sm.Path, sm.Url, sm.HeadCommitId?.Sha, sm.WorkDirCommitId?.Sha, state));
            }

            var workDir = info.WorkingDirectory ?? info.Path;
            return new RepositorySnapshot(
                workDir.TrimEnd('/', '\\'),
                Path.GetFileName(workDir.TrimEnd('/', '\\')),
                detached ? null : repo.Head.FriendlyName,
                headSha,
                detached,
                info.IsBare,
                MapOperation(info.CurrentOperation),
                local.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                remote.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                tags.OrderByDescending(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                remotes,
                stashes,
                submodules);
        }, cancellationToken);

    public Task<WorkingCopyStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ctx.Lib.RunAsync(repo =>
        {
            var options = new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
                DetectRenamesInIndex = true,
                DetectRenamesInWorkDir = false,
                IncludeIgnored = false,
            };

            var staged = new List<FileStatusEntry>();
            var unstaged = new List<FileStatusEntry>();
            var conflicted = new List<FileStatusEntry>();

            foreach (var e in repo.RetrieveStatus(options))
            {
                var s = e.State;
                if (s.HasFlag(LgStatus.Conflicted))
                {
                    conflicted.Add(new FileStatusEntry(e.FilePath, null, FileChangeKind.Conflicted, StagingArea.Unstaged));
                    continue;
                }

                var indexKind = s switch
                {
                    _ when s.HasFlag(LgStatus.NewInIndex) => FileChangeKind.Added,
                    _ when s.HasFlag(LgStatus.RenamedInIndex) => FileChangeKind.Renamed,
                    _ when s.HasFlag(LgStatus.DeletedFromIndex) => FileChangeKind.Deleted,
                    _ when s.HasFlag(LgStatus.TypeChangeInIndex) => FileChangeKind.TypeChanged,
                    _ when s.HasFlag(LgStatus.ModifiedInIndex) => FileChangeKind.Modified,
                    _ => FileChangeKind.Unmodified,
                };
                if (indexKind != FileChangeKind.Unmodified)
                {
                    staged.Add(new FileStatusEntry(e.FilePath, e.HeadToIndexRenameDetails?.OldFilePath, indexKind, StagingArea.Staged));
                }

                var workKind = s switch
                {
                    _ when s.HasFlag(LgStatus.NewInWorkdir) => FileChangeKind.Untracked,
                    _ when s.HasFlag(LgStatus.DeletedFromWorkdir) => FileChangeKind.Deleted,
                    _ when s.HasFlag(LgStatus.TypeChangeInWorkdir) => FileChangeKind.TypeChanged,
                    _ when s.HasFlag(LgStatus.RenamedInWorkdir) => FileChangeKind.Renamed,
                    _ when s.HasFlag(LgStatus.ModifiedInWorkdir) => FileChangeKind.Modified,
                    _ => FileChangeKind.Unmodified,
                };
                if (workKind != FileChangeKind.Unmodified)
                {
                    unstaged.Add(new FileStatusEntry(e.FilePath, e.IndexToWorkDirRenameDetails?.OldFilePath, workKind, StagingArea.Unstaged));
                }
            }

            static List<FileStatusEntry> Sort(List<FileStatusEntry> l) => l.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return new WorkingCopyStatus(Sort(staged), Sort(unstaged), Sort(conflicted));
        }, cancellationToken);

    /// <summary>
    /// Streams history via <c>git log</c>: core Git uses the commit-graph file and generation numbers, and streaming
    /// lets the first screen render before a 100k-commit history has finished loading.
    /// </summary>
    public async IAsyncEnumerable<CommitInfo> GetHistoryAsync(HistoryQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "log", "--date-order", "--no-show-signature", $"--format=%H{FieldSep}%P{FieldSep}%an{FieldSep}%ae{FieldSep}%at{FieldSep}%s{RecordSep}" };
        if (query.MaxCount is { } max) args.Add($"--max-count={max}");
        if (query.Since is { } since) args.Add($"--since={since.ToUnixTimeSeconds()}");
        if (query.Until is { } until) args.Add($"--until={until.ToUnixTimeSeconds()}");

        if (query.StartRef is { Length: > 0 } start)
        {
            args.Add("--end-of-options");
            args.Add(start);
        }
        else if (query.AllRefs)
        {
            // Branches, remotes, tags, and HEAD; excludes refs/stash so stash internals don't clutter the graph.
            args.AddRange(["--branches", "--remotes", "--tags", "HEAD"]);
        }
        else
        {
            args.Add("HEAD");
        }

        args.Add("--");
        if (!string.IsNullOrWhiteSpace(query.PathFilter)) args.Add(query.PathFilter);

        var unborn = await ctx.Lib.RunAsync(r => r.Info.IsHeadUnborn, cancellationToken).ConfigureAwait(false);
        if (unborn && query.StartRef is null)
        {
            if (!query.AllRefs) yield break;
            args.Remove("HEAD");
            var hasRefs = await ctx.Lib.RunAsync(r => r.Refs.Any(), cancellationToken).ConfigureAwait(false);
            if (!hasRefs) yield break;
        }

        var cmd = new GitCommand(ctx.WorkDir, args);
        await foreach (var raw in ctx.Cli.StreamAsync(cmd, RecordSep, cancellationToken).ConfigureAwait(false))
        {
            var record = raw.Trim('\n', '\r');
            if (record.Length == 0) continue;
            var f = record.Split(FieldSep);
            if (f.Length < 6) continue;
            var parents = f[1].Length == 0 ? [] : f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var when = DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[4], CultureInfo.InvariantCulture));
            yield return new CommitInfo(f[0], parents, f[2], f[3], when, f[5]);
        }
    }

    public Task<CommitDetails> GetCommitDetailsAsync(string sha, CancellationToken cancellationToken = default) =>
        ctx.Lib.RunAsync(repo =>
        {
            var c = repo.Lookup<Commit>(sha) ?? throw new InvalidOperationException($"Commit {sha} not found.");
            var parent = c.Parents.FirstOrDefault();
            var compare = new LibGit2Sharp.CompareOptions { Similarity = SimilarityOptions.Renames };
            var changes = repo.Diff.Compare<TreeChanges>(parent?.Tree, c.Tree, compare)
                .Select(ch => new FileChange(ch.Path, ch.Status is ChangeKind.Renamed or ChangeKind.Copied ? ch.OldPath : null, Map(ch.Status)))
                .OrderBy(ch => ch.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var summary = new CommitInfo(c.Sha, c.Parents.Select(p => p.Sha).ToList(), c.Author.Name, c.Author.Email, c.Author.When, c.MessageShort);
            return new CommitDetails(summary, c.Message, c.Committer.Name, c.Committer.Email, c.Committer.When, changes);
        }, cancellationToken);

    public async Task<FileDiff?> GetWorkingDiffAsync(FileStatusEntry file, DiffOptions options, CancellationToken cancellationToken = default)
    {
        if (file.Kind == FileChangeKind.Conflicted) return null;

        GitResult result;
        if (file.Kind == FileChangeKind.Untracked)
        {
            // Show the whole new file; git treats "/dev/null" specially in --no-index mode on every platform.
            result = await ctx.RunAsync(
                ["diff", "--no-index", "--no-color", "--no-ext-diff", "--", "/dev/null", file.Path],
                cancellationToken, okCodes: [0, 1]).ConfigureAwait(false);
        }
        else
        {
            var args = new List<string> { "--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", "--no-textconv", $"-U{options.ContextLines}" };
            if (file.Area == StagingArea.Staged) args.AddRange(["--cached", "-M"]);
            if (options.IgnoreWhitespace) args.Add("-w");
            args.Add("--");
            if (file.OldPath is not null) args.Add(file.OldPath);
            args.Add(file.Path);
            result = await ctx.RunAsync(args, cancellationToken).ConfigureAwait(false);
        }

        result.EnsureSuccess("diff");
        return UnifiedDiffParser.ParseSingle(result.StandardOutput);
    }

    public async Task<FileDiff?> GetCommitDiffAsync(string sha, FileChange change, DiffOptions options, CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "--literal-pathspecs", "show", "--format=", "--no-color", "--no-ext-diff", "-M",
            "--diff-merges=first-parent", $"-U{options.ContextLines}",
        };
        if (options.IgnoreWhitespace) args.Add("-w");
        args.AddRange(["--end-of-options", sha, "--"]);
        if (change.OldPath is not null) args.Add(change.OldPath);
        args.Add(change.Path);

        var result = (await ctx.RunAsync(args, cancellationToken).ConfigureAwait(false)).EnsureSuccess("show");
        return UnifiedDiffParser.ParseSingle(result.StandardOutput);
    }

    public Task<IReadOnlyList<ReflogEntryInfo>> GetReflogAsync(string refName = "HEAD", int max = 500, CancellationToken cancellationToken = default) =>
        ctx.Lib.RunAsync<IReadOnlyList<ReflogEntryInfo>>(repo =>
            repo.Refs.Log(refName)
                .Take(max)
                .Select((e, i) => new ReflogEntryInfo(i, e.From.Sha, e.To.Sha, e.Message ?? string.Empty, e.Committer.Name, e.Committer.When))
                .ToList(), cancellationToken);

    /// <summary>Dangling commits not reachable from any ref or reflog: candidates for disaster recovery.</summary>
    public async Task<IReadOnlyList<string>> GetUnreachableCommitsAsync(CancellationToken cancellationToken = default)
    {
        var result = await ctx.RunAsync(["fsck", "--unreachable", "--no-reflogs", "--no-progress"], cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("fsck");
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("unreachable commit ", StringComparison.Ordinal))
            .Select(l => l["unreachable commit ".Length..].Trim())
            .ToList();
    }

    private static string? ShortenUpstream(string? canonical, string? remote)
    {
        if (canonical is null || remote is null) return null;
        const string heads = "refs/heads/";
        return canonical.StartsWith(heads, StringComparison.Ordinal) ? $"{remote}/{canonical[heads.Length..]}" : canonical;
    }

    private static FileChangeKind Map(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => FileChangeKind.Added,
        ChangeKind.Deleted => FileChangeKind.Deleted,
        ChangeKind.Modified => FileChangeKind.Modified,
        ChangeKind.Renamed => FileChangeKind.Renamed,
        ChangeKind.Copied => FileChangeKind.Copied,
        ChangeKind.TypeChanged => FileChangeKind.TypeChanged,
        ChangeKind.Conflicted => FileChangeKind.Conflicted,
        ChangeKind.Untracked => FileChangeKind.Untracked,
        ChangeKind.Ignored => FileChangeKind.Ignored,
        _ => FileChangeKind.Unmodified,
    };

    internal static RepositoryOperation MapOperation(CurrentOperation op) => op switch
    {
        CurrentOperation.Merge => RepositoryOperation.Merge,
        CurrentOperation.Revert or CurrentOperation.RevertSequence => RepositoryOperation.Revert,
        CurrentOperation.CherryPick or CurrentOperation.CherryPickSequence => RepositoryOperation.CherryPick,
        CurrentOperation.Bisect => RepositoryOperation.Bisect,
        CurrentOperation.RebaseInteractive => RepositoryOperation.RebaseInteractive,
        CurrentOperation.Rebase or CurrentOperation.RebaseMerge => RepositoryOperation.Rebase,
        CurrentOperation.ApplyMailbox or CurrentOperation.ApplyMailboxOrRebase => RepositoryOperation.ApplyMailbox,
        _ => RepositoryOperation.None,
    };
}
