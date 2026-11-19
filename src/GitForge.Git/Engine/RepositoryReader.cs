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

    public async Task<RepositorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Remotes.Count > 0 ? snapshot : await WithRemotesFromGitAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// libgit2 lists no remotes for a linked worktree (git worktree add) although git uses the main repository's. Read them
    /// from git instead, and name the remote of each remote-tracking branch, so fetch, pull, and push work in worktree tabs.
    /// </summary>
    private async Task<RepositorySnapshot> WithRemotesFromGitAsync(RepositorySnapshot snapshot, CancellationToken ct)
    {
        var r = await ctx.RunAsync(["config", "--get-regexp", @"^remote\..*\.(url|pushurl)$"], ct, okCodes: [0, 1]).ConfigureAwait(false);
        var urls = new Dictionary<string, (string? Url, string? PushUrl)>(StringComparer.Ordinal);
        foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0) continue;
            var key = line[..space];
            var value = line[(space + 1)..];
            var dot = key.LastIndexOf('.');
            var name = key["remote.".Length..dot];
            var current = urls.GetValueOrDefault(name);
            urls[name] = key.EndsWith(".pushurl", StringComparison.OrdinalIgnoreCase) ? current with { PushUrl = value } : current with { Url = value };
        }

        if (urls.Count == 0) return snapshot;
        var resolver = ctx.Options.SelfHostedResolver;
        var remotes = urls.Where(u => u.Value.Url is not null)
            .Select(u => new RemoteInfo(u.Key, u.Value.Url!, u.Value.PushUrl, RemoteUrlParser.Parse(u.Value.Url!, resolver)))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
        var names = remotes.Select(x => x.Name).OrderByDescending(n => n.Length).ToList(); // longest first: "origin-mirror" before "origin"
        var branches = snapshot.RemoteBranches
            .Select(b => b.RemoteName is not null ? b : b with { RemoteName = names.FirstOrDefault(n => b.Name.StartsWith(n + "/", StringComparison.Ordinal)) })
            .ToList();
        return snapshot with { Remotes = remotes, RemoteBranches = branches };
    }

    private Task<RepositorySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
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
                DetectOperation(repo),
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

        string? stdin = null;
        if (query.StartRef is { Length: > 0 } start)
        {
            args.Add("--end-of-options");
            args.Add(start);
        }
        else if (query.Refs is { Count: > 0 } refs)
        {
            // Read tips from stdin: no command-line length limit, and names can never be parsed as options.
            args.Add("--stdin");
            stdin = string.Join('\n', refs.Where(r => !r.StartsWith('-'))) + "\n";
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
        if (unborn && query.StartRef is null && stdin is null)
        {
            if (!query.AllRefs) yield break;
            args.Remove("HEAD");
            var hasRefs = await ctx.Lib.RunAsync(r => r.Refs.Any(), cancellationToken).ConfigureAwait(false);
            if (!hasRefs) yield break;
        }

        var cmd = new GitCommand(ctx.WorkDir, args) { StandardInput = stdin };
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

    /// <summary>
    /// <c>git log --follow --name-status</c>: records start with \x1e, then the commit fields, then one name-status line
    /// giving the file's name in that commit (so renamed files keep resolving).
    /// </summary>
    public async IAsyncEnumerable<FileHistoryEntry> GetFileHistoryAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "--literal-pathspecs", "log", "--follow", "-M", "--name-status", "--no-show-signature",
            $"--format={RecordSep}%H{FieldSep}%P{FieldSep}%an{FieldSep}%ae{FieldSep}%at{FieldSep}%s", "--", path,
        };

        var currentPath = path;
        await foreach (var raw in ctx.Cli.StreamAsync(new GitCommand(ctx.WorkDir, args), RecordSep, cancellationToken).ConfigureAwait(false))
        {
            var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;
            var f = lines[0].TrimEnd('\r').Split(FieldSep);
            if (f.Length < 6) continue;

            var kind = FileChangeKind.Modified;
            string? oldPath = null;
            var status = lines.Skip(1).Select(l => l.TrimEnd('\r').Split('\t')).FirstOrDefault(p => p.Length >= 2);
            if (status is not null)
            {
                if (StashOperations.ParseNameStatus(string.Join('\t', status)) is [var parsed, ..]) kind = parsed.Kind;
                if (status.Length >= 3)
                {
                    oldPath = status[1];
                    currentPath = status[2];
                }
                else
                {
                    currentPath = status[1];
                }
            }

            var parents = f[1].Length == 0 ? [] : f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var commit = new CommitInfo(f[0], parents, f[2], f[3], DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[4], CultureInfo.InvariantCulture)), f[5]);
            yield return new FileHistoryEntry(commit, currentPath, oldPath, kind);
            if (oldPath is not null) currentPath = oldPath; // older commits know the file by its previous name
        }
    }

    public async Task<string?> GetMergeBaseAsync(string a, string b, CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["merge-base", BranchOperations.Guard(a), BranchOperations.Guard(b)], cancellationToken, okCodes: [0, 1]).ConfigureAwait(false);
        var sha = r.StandardOutput.Trim();
        return r.ExitCode == 0 && sha.Length >= 40 ? sha : null; // exit 1 = no common ancestor
    }

    public async Task<IReadOnlyList<FileChange>> GetChangesBetweenAsync(string fromRevision, string? toRevision, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "diff", "--name-status", "-M", "--no-ext-diff", BranchOperations.Guard(fromRevision) };
        if (toRevision is not null) args.Add(BranchOperations.Guard(toRevision));
        args.Add("--");
        var r = await ctx.RunAsync(args, cancellationToken).ConfigureAwait(false);
        return StashOperations.ParseNameStatus(r.EnsureSuccess("diff").StandardOutput)
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<FileDiff?> GetDiffBetweenAsync(string fromRevision, string? toRevision, FileChange change, DiffOptions options, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", "-M", $"-U{options.ContextLines}" };
        if (options.IgnoreWhitespace) args.Add("-w");
        args.Add(BranchOperations.Guard(fromRevision));
        if (toRevision is not null) args.Add(BranchOperations.Guard(toRevision));
        args.Add("--");
        if (change.OldPath is not null) args.Add(change.OldPath);
        args.Add(change.Path);
        var r = await ctx.RunAsync(args, cancellationToken).ConfigureAwait(false);
        return UnifiedDiffParser.ParseSingle(r.EnsureSuccess("diff").StandardOutput);
    }

    public async Task<BlameResult> GetBlameAsync(string path, string? revision, CancellationToken cancellationToken = default)
    {
        // git blame does not honor --end-of-options before <rev>, so reject option-like revisions explicitly.
        var args = new List<string> { "blame", "--porcelain" };
        if (revision is { Length: > 0 }) args.Add(BranchOperations.Guard(revision));
        args.AddRange(["--", path]);
        var result = await ctx.RunAsync(args, cancellationToken).ConfigureAwait(false);
        return ParseBlame(result.EnsureSuccess("blame").StandardOutput);
    }

    /// <summary>
    /// Parses <c>git blame --porcelain</c>: a header "&lt;sha&gt; &lt;orig&gt; &lt;final&gt; [&lt;count&gt;]", key/value lines the
    /// first time a commit appears, then the content line prefixed with a tab.
    /// </summary>
    internal static BlameResult ParseBlame(string output)
    {
        var lines = new List<BlameLine>();
        var commits = new Dictionary<string, BlameCommit>(StringComparer.Ordinal);
        string? sha = null;
        var final = 0;
        string author = string.Empty, email = string.Empty, summary = string.Empty;
        long time = 0;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.StartsWith('\t'))
            {
                if (sha is null) continue;
                if (!commits.ContainsKey(sha))
                {
                    commits[sha] = new BlameCommit(sha, author, email.Trim('<', '>'), DateTimeOffset.FromUnixTimeSeconds(time), summary);
                }

                lines.Add(new BlameLine(final, sha, line[1..]));
                sha = null;
                continue;
            }

            if (sha is null)
            {
                var parts = line.Split(' ');
                if (parts.Length >= 3 && parts[0].Length >= 40 && parts[0].All(Uri.IsHexDigit))
                {
                    sha = parts[0];
                    final = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    author = email = summary = string.Empty;
                    time = 0;
                }

                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];
            switch (key)
            {
                case "author": author = value; break;
                case "author-mail": email = value; break;
                case "author-time": time = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 0; break;
                case "summary": summary = value; break;
            }
        }

        return new BlameResult(lines, commits);
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

    /// <summary>
    /// The operation in progress. A multi-commit cherry-pick or revert that stopped without a conflict (for example,
    /// refused because local changes were in the way) leaves only .git/sequencer behind, which libgit2 does not report;
    /// git still treats it as in progress and refuses new ones, so it is surfaced too (continue or abort clears it).
    /// </summary>
    internal static RepositoryOperation DetectOperation(Repository repo)
    {
        var op = MapOperation(repo.Info.CurrentOperation);
        if (op != RepositoryOperation.None) return op;
        var todo = Path.Combine(repo.Info.Path, "sequencer", "todo");
        if (!File.Exists(todo)) return RepositoryOperation.None;
        var next = File.ReadLines(todo).FirstOrDefault(l => l.Length > 0 && l[0] != '#') ?? string.Empty;
        return next.StartsWith("revert", StringComparison.Ordinal) ? RepositoryOperation.Revert : RepositoryOperation.CherryPick;
    }

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
