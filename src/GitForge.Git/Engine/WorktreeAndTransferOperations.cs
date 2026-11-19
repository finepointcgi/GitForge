using System.Globalization;
using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Git.Engine;

/// <summary><c>git worktree</c>: several branches checked out at once, each in its own folder, sharing one repository.</summary>
internal sealed class WorktreeOperations(RepoContext ctx) : IWorktreeOperations
{
    public async Task<IReadOnlyList<WorktreeInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["worktree", "list", "--porcelain", "-z"], cancellationToken).ConfigureAwait(false);
        return GitOutput.ParseWorktrees(r.EnsureSuccess("worktree list").StandardOutput, ctx.WorkDir);
    }

    public Task AddAsync(string path, string branch, string? newBranch, CancellationToken cancellationToken = default)
    {
        var full = FullPath(path);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new InvalidOperationException($"{full} already exists and is not empty. Choose a new folder.");
        }

        var args = new List<string> { "worktree", "add" };
        if (!string.IsNullOrWhiteSpace(newBranch)) args.AddRange(["-b", BranchOperations.Valid(newBranch.Trim())]);
        args.AddRange(["--", full, BranchOperations.Guard(branch)]);
        return ctx.WriteAsync("worktree add", args, cancellationToken);
    }

    public async Task RemoveAsync(string path, bool force, CancellationToken cancellationToken = default)
    {
        var full = FullPath(path);
        var result = await ctx.TryWriteAsync(force ? ["worktree", "remove", "--force", "--", full] : ["worktree", "remove", "--", full], cancellationToken, env: RepoContext.EnglishMessages).ConfigureAwait(false);
        if (result.Succeeded) return;

        // git refuses worktrees that contain submodules, even with --force. Forced, do what git documents instead:
        // delete the folder, then prune. Only ever for a linked worktree, never the main one.
        if (force && (result.StandardError + result.StandardOutput).Contains("containing submodules", StringComparison.Ordinal))
        {
            var linked = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(w => !w.IsMain && GitOutput.SamePath(w.Path, full));
            if (linked is null || GitOutput.SamePath(full, ctx.WorkDir)) throw new InvalidOperationException($"{full} is not a linked worktree of this repository.");
            DeleteTree(full);
            await PruneAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        result.EnsureSuccess("worktree remove");
    }

    /// <summary>Deletes a folder tree, clearing read-only attributes first (git marks some files read-only).</summary>
    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    public Task PruneAsync(CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("worktree prune", ["worktree", "prune"], cancellationToken);

    public async Task<bool> HasChangesAsync(string path, CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["--no-optional-locks", "-C", FullPath(path), "status", "--porcelain", "--ignore-submodules=none", "--untracked-files=normal"], cancellationToken).ConfigureAwait(false);
        return r.EnsureSuccess("status").StandardOutput.Trim().Length > 0;
    }

    private static string FullPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Choose a folder for the worktree.", nameof(path)) : Path.GetFullPath(path.Trim().Trim('"'));
}

/// <summary>Patches (<c>git format-patch</c>, <c>git am</c>, <c>git apply</c>) and bundles (<c>git bundle</c>).</summary>
internal sealed class TransferOperations(RepoContext ctx) : ITransferOperations
{
    /// <summary>Accept default messages (a stopped <c>git am</c> continuing) without opening an editor.</summary>
    private static readonly Dictionary<string, string> NoEditor = new() { ["GIT_EDITOR"] = "true" };

    public async Task<IReadOnlyList<string>> SaveCommitsAsPatchesAsync(IReadOnlyList<string> shasOldestFirst, string directory, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        for (var i = 0; i < shasOldestFirst.Count; i++)
        {
            var r = await ctx.RunAsync(
                ["format-patch", "-1", "--start-number", (i + 1).ToString(CultureInfo.InvariantCulture), "-o", dir, "--end-of-options", BranchOperations.Guard(shasOldestFirst[i])],
                cancellationToken).ConfigureAwait(false);
            files.AddRange(r.EnsureSuccess("format-patch").StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        return files;
    }

    public async Task<bool> SaveUncommittedPatchAsync(string file, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var head = await ctx.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], ct, okCodes: [0, 1, 128]).ConfigureAwait(false);
        if (head.ExitCode != 0) throw new InvalidOperationException("Commit at least once before creating a patch.");

        // git writes the patch files itself (--output), so content in any encoding survives byte for byte.
        var temp = Directory.CreateTempSubdirectory("gitforge-patch-");
        try
        {
            var parts = new List<string>();
            var tracked = Path.Combine(temp.FullName, "tracked.patch");
            (await ctx.RunAsync(["diff", "--binary", "--no-color", "--no-ext-diff", "--no-textconv", $"--output={tracked}", "HEAD", "--"], ct).ConfigureAwait(false)).EnsureSuccess("diff");
            parts.Add(tracked);

            // New files are not in "git diff HEAD"; add each as a creation patch without touching the index.
            var untracked = await ctx.RunAsync(["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
            var index = 0;
            foreach (var path in untracked.EnsureSuccess("ls-files").StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = Path.Combine(temp.FullName, $"new-{index++}.patch");
                (await ctx.RunAsync(["diff", "--binary", "--no-color", "--no-ext-diff", "--no-index", $"--output={part}", "--", "/dev/null", path], ct, okCodes: [0, 1]).ConfigureAwait(false)).EnsureSuccess("diff");
                parts.Add(part);
            }

            if (parts.All(p => !File.Exists(p) || new FileInfo(p).Length == 0)) return false;
            var full = Path.GetFullPath(file.Trim().Trim('"'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using var output = File.Create(full);
            foreach (var p in parts.Where(File.Exists))
            {
                await using var input = File.OpenRead(p);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            return true;
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
    }

    public async Task<GitResult> ApplyPatchAsync(string file, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(file.Trim().Trim('"'));
        if (!File.Exists(full)) throw new FileNotFoundException($"{full} does not exist.", full);

        var start = new char[64 * 1024];
        int read;
        using (var reader = new StreamReader(full))
        {
            read = await reader.ReadBlockAsync(start.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        // --keep-cr: patches of CRLF files carry "\r\n" lines that must survive mailbox splitting.
        return GitOutput.IsMailbox(new string(start, 0, read))
            ? await ctx.TryWriteAsync(["am", "--3way", "--keep-cr", "--", full], cancellationToken, env: NoEditor).ConfigureAwait(false)
            : await ctx.TryWriteAsync(["apply", "--3way", "--whitespace=nowarn", "--", full], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> CreateBundleAsync(string file, IReadOnlyList<string> refs, string? basis, CancellationToken cancellationToken = default)
    {
        if (refs.Count == 0) throw new ArgumentException("Choose what to put in the bundle.", nameof(refs));
        var full = Path.GetFullPath(file.Trim().Trim('"'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var args = new List<string> { "bundle", "create", full };
        foreach (var r in refs) args.Add(r is "--branches" or "--tags" or "--all" ? r : BranchOperations.Guard(r));
        if (!string.IsNullOrWhiteSpace(basis)) args.Add("^" + BranchOperations.Guard(basis.Trim()));
        (await ctx.RunAsync(args, cancellationToken).ConfigureAwait(false)).EnsureSuccess("bundle create");

        var heads = await ctx.RunAsync(["bundle", "list-heads", full], cancellationToken).ConfigureAwait(false);
        return heads.EnsureSuccess("bundle list-heads").StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2) is [_, var name] ? name : l)
            .ToList();
    }

    public async Task FetchBundleAsync(string file, string remoteName, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var full = Path.GetFullPath(file.Trim().Trim('"'));
        if (!File.Exists(full)) throw new FileNotFoundException($"{full} does not exist.", full);
        var name = BranchOperations.Valid(remoteName.Trim());

        // Fails with the missing prerequisite commits when the bundle builds on history this repository lacks.
        (await ctx.RunAsync(["bundle", "verify", full], ct).ConfigureAwait(false)).EnsureSuccess("bundle verify");

        var remotes = (await ctx.RunAsync(["remote"], ct).ConfigureAwait(false)).EnsureSuccess("remote").StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (remotes.Contains(name, StringComparer.Ordinal))
        {
            await ctx.WriteAsync("set remote URL", ["remote", "set-url", "--", name, full], ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.WriteAsync("add remote", ["remote", "add", "--", name, full], ct).ConfigureAwait(false);
        }

        await ctx.WriteAsync("fetch bundle", ["fetch", "--progress", name], ct, progress: progress).ConfigureAwait(false);
    }
}
