using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Git.Undo;

/// <summary>One reversible action. Guards refuse to undo/redo if the repository moved on since the action ran.</summary>
public sealed record UndoEntry(
    string Description,
    Func<IGitRepository, CancellationToken, Task> Undo,
    Func<IGitRepository, CancellationToken, Task> Redo);

/// <summary>
/// Single-level undo/redo (the most recent supported action), mirroring how desktop Git clients scope undo:
/// a new action replaces the undo slot and clears redo.
/// </summary>
public sealed class UndoManager
{
    private UndoEntry? _undo;
    private UndoEntry? _redo;

    public event EventHandler? Changed;

    public string? UndoDescription => _undo?.Description;
    public string? RedoDescription => _redo?.Description;
    public bool CanUndo => _undo is not null;
    public bool CanRedo => _redo is not null;

    public void Record(UndoEntry? entry)
    {
        if (entry is null) return;
        _undo = entry;
        _redo = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo = _redo = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> UndoAsync(IGitRepository repo, CancellationToken ct = default)
    {
        var e = _undo ?? throw new InvalidOperationException("Nothing to undo.");
        await e.Undo(repo, ct).ConfigureAwait(false);
        _undo = null;
        _redo = e;
        Changed?.Invoke(this, EventArgs.Empty);
        return e.Description;
    }

    public async Task<string> RedoAsync(IGitRepository repo, CancellationToken ct = default)
    {
        var e = _redo ?? throw new InvalidOperationException("Nothing to redo.");
        await e.Redo(repo, ct).ConfigureAwait(false);
        _redo = null;
        _undo = e;
        Changed?.Invoke(this, EventArgs.Empty);
        return e.Description;
    }
}

/// <summary>Runs an operation and returns the entry that reverses it (or null when the action cannot be undone safely).</summary>
public static class UndoableOperations
{
    private const long MaxDiscardSnapshotBytes = 64L * 1024 * 1024;

    public static async Task<UndoEntry?> CommitAsync(IGitRepository repo, CommitRequest request, CancellationToken ct = default)
    {
        var before = await HeadShaAsync(repo, ct).ConfigureAwait(false);
        var after = await repo.Index.CommitAsync(request, ct).ConfigureAwait(false);
        if (before is null) return null; // first commit: nothing to reset back to

        var subject = request.Message.Split('\n')[0].Trim();
        return new UndoEntry(
            request.Amend ? "amend commit" : $"commit \"{Truncate(subject, 40)}\"",
            async (r, c) =>
            {
                await EnsureHeadAsync(r, after, c).ConfigureAwait(false);
                await r.History.ResetAsync(before, ResetMode.Soft, c).ConfigureAwait(false);
            },
            async (r, c) =>
            {
                await EnsureHeadAsync(r, before, c).ConfigureAwait(false);
                await r.History.ResetAsync(after, ResetMode.Soft, c).ConfigureAwait(false);
            });
    }

    public static async Task<UndoEntry?> CheckoutAsync(IGitRepository repo, string target, CancellationToken ct = default)
    {
        var previous = await CurrentRefAsync(repo, ct).ConfigureAwait(false);
        await repo.Branches.CheckoutAsync(target, ct).ConfigureAwait(false);
        if (previous is null || previous == target) return null;
        return new UndoEntry(
            $"checkout {Short(target)}",
            (r, c) => r.Branches.CheckoutAsync(previous, c),
            (r, c) => r.Branches.CheckoutAsync(target, c));
    }

    public static async Task<UndoEntry?> CreateBranchAsync(IGitRepository repo, string name, string startPoint, bool checkout, CancellationToken ct = default)
    {
        var previous = checkout ? await CurrentRefAsync(repo, ct).ConfigureAwait(false) : null;
        await repo.Branches.CreateAsync(name, startPoint, checkout, ct).ConfigureAwait(false);
        var tip = await RevParseAsync(repo, $"refs/heads/{name}", ct).ConfigureAwait(false);
        return new UndoEntry(
            $"create branch {name}",
            async (r, c) =>
            {
                if (await RevParseAsync(r, $"refs/heads/{name}", c).ConfigureAwait(false) != tip)
                {
                    throw new InvalidOperationException($"Branch {name} has new commits; undo would lose them.");
                }

                if (previous is not null) await r.Branches.CheckoutAsync(previous, c).ConfigureAwait(false);
                await r.Branches.DeleteAsync(name, force: true, c).ConfigureAwait(false);
            },
            (r, c) => r.Branches.CreateAsync(name, tip!, checkout, c));
    }

    public static async Task<UndoEntry?> DeleteBranchAsync(IGitRepository repo, string name, bool force, CancellationToken ct = default)
    {
        var tip = await RevParseAsync(repo, $"refs/heads/{name}", ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Branch {name} not found.");
        var upstream = (await repo.QueryAsync(["rev-parse", "--abbrev-ref", "--symbolic-full-name", $"{name}@{{upstream}}"], ct).ConfigureAwait(false)) is { Succeeded: true } u
            ? u.StandardOutput.Trim() : null;
        await repo.Branches.DeleteAsync(name, force, ct).ConfigureAwait(false);
        return new UndoEntry(
            $"delete branch {name}",
            async (r, c) =>
            {
                await r.Branches.CreateAsync(name, tip, checkout: false, c).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(upstream)) await r.Branches.SetUpstreamAsync(name, upstream, c).ConfigureAwait(false);
            },
            (r, c) => r.Branches.DeleteAsync(name, force: true, c));
    }

    public static async Task<UndoEntry?> ResetAsync(IGitRepository repo, string target, ResetMode mode, CancellationToken ct = default)
    {
        var before = await HeadShaAsync(repo, ct).ConfigureAwait(false);
        await repo.History.ResetAsync(target, mode, ct).ConfigureAwait(false);
        var after = await HeadShaAsync(repo, ct).ConfigureAwait(false);
        if (before is null || after is null) return null;
        return new UndoEntry(
            $"reset ({mode.ToString().ToLowerInvariant()}) to {Short(target)}",
            async (r, c) =>
            {
                await EnsureHeadAsync(r, after, c).ConfigureAwait(false);
                if (mode == ResetMode.Hard) await EnsureCleanAsync(r, c).ConfigureAwait(false);
                await r.History.ResetAsync(before, mode, c).ConfigureAwait(false);
            },
            async (r, c) =>
            {
                await EnsureHeadAsync(r, before, c).ConfigureAwait(false);
                if (mode == ResetMode.Hard) await EnsureCleanAsync(r, c).ConfigureAwait(false);
                await r.History.ResetAsync(after, mode, c).ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Discards working-tree changes after snapshotting the affected files' bytes in memory, so undo can
    /// restore them exactly (including untracked files, which git itself cannot recover).
    /// </summary>
    public static async Task<UndoEntry?> DiscardAsync(IGitRepository repo, Func<Task> discard, IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var snapshot = await SnapshotAsync(repo.WorkingDirectory, paths, ct).ConfigureAwait(false);
        await discard().ConfigureAwait(false);
        if (snapshot is null) return null; // too large to keep in memory

        var afterDiscard = await SnapshotAsync(repo.WorkingDirectory, paths, ct).ConfigureAwait(false);
        var label = paths.Count == 1 ? $"discard {Path.GetFileName(paths.First())}" : $"discard {paths.Count} files";
        return new UndoEntry(
            label,
            async (r, c) =>
            {
                var now = await SnapshotAsync(r.WorkingDirectory, paths, c).ConfigureAwait(false);
                if (!SameContent(now, afterDiscard)) throw new InvalidOperationException("The files changed after the discard; undo would overwrite your newer edits.");
                await RestoreAsync(r.WorkingDirectory, snapshot, c).ConfigureAwait(false);
            },
            async (r, c) =>
            {
                var now = await SnapshotAsync(r.WorkingDirectory, paths, c).ConfigureAwait(false);
                if (!SameContent(now, snapshot)) throw new InvalidOperationException("The files changed after the undo; redo would overwrite your newer edits.");
                await RestoreAsync(r.WorkingDirectory, afterDiscard!, c).ConfigureAwait(false);
            });
    }

    private static async Task<Dictionary<string, byte[]?>?> SnapshotAsync(string root, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        long total = 0;
        foreach (var p in paths)
        {
            var full = SafePath(root, p);
            if (File.Exists(full))
            {
                total += new FileInfo(full).Length;
                if (total > MaxDiscardSnapshotBytes) return null;
                result[p] = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            }
            else
            {
                result[p] = null; // file did not exist
            }
        }

        return result;
    }

    private static async Task RestoreAsync(string root, Dictionary<string, byte[]?> snapshot, CancellationToken ct)
    {
        foreach (var (p, bytes) in snapshot)
        {
            var full = SafePath(root, p);
            if (bytes is null)
            {
                if (File.Exists(full)) File.Delete(full);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);
        }
    }

    private static bool SameContent(Dictionary<string, byte[]?>? a, Dictionary<string, byte[]?>? b)
    {
        if (a is null || b is null || a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var w)) return false;
            if (v is null || w is null)
            {
                if (v != w) return false;
                continue;
            }

            if (!v.AsSpan().SequenceEqual(w)) return false;
        }

        return true;
    }

    /// <summary>Resolves a repository-relative path and rejects anything escaping the working tree.</summary>
    private static string SafePath(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Path escapes the repository: {relative}");
        return full;
    }

    internal static async Task<string?> HeadShaAsync(IGitRepository repo, CancellationToken ct) =>
        await RevParseAsync(repo, "HEAD", ct).ConfigureAwait(false);

    private static async Task<string?> RevParseAsync(IGitRepository repo, string rev, CancellationToken ct)
    {
        var r = await repo.QueryAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", rev], ct).ConfigureAwait(false);
        return r.Succeeded ? r.StandardOutput.Trim() : null;
    }

    /// <summary>Current branch name, or the detached SHA.</summary>
    private static async Task<string?> CurrentRefAsync(IGitRepository repo, CancellationToken ct)
    {
        var r = await repo.QueryAsync(["symbolic-ref", "--short", "-q", "HEAD"], ct).ConfigureAwait(false);
        return r.Succeeded && r.StandardOutput.Trim().Length > 0 ? r.StandardOutput.Trim() : await HeadShaAsync(repo, ct).ConfigureAwait(false);
    }

    private static async Task EnsureHeadAsync(IGitRepository repo, string expected, CancellationToken ct)
    {
        if (await HeadShaAsync(repo, ct).ConfigureAwait(false) != expected)
        {
            throw new InvalidOperationException("HEAD has moved since this action; undo/redo is no longer safe.");
        }
    }

    private static async Task EnsureCleanAsync(IGitRepository repo, CancellationToken ct)
    {
        if (!(await repo.Read.GetStatusAsync(ct).ConfigureAwait(false)).IsClean)
        {
            throw new InvalidOperationException("You have uncommitted changes; commit or stash them before undoing a hard reset.");
        }
    }

    private static string Short(string rev) => rev.Length == 40 && rev.All(Uri.IsHexDigit) ? rev[..8] : rev;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
