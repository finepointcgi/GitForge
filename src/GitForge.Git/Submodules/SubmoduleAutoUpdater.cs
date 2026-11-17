using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Git.Submodules;

/// <summary>
/// Keeps submodules in step with the superproject, recursively, without clobbering work in progress.
/// </summary>
/// <remarks>
/// Policy (equivalent to <c>submodule.recurse</c>, plus init and sync):
/// <list type="bullet">
/// <item>After an operation moves HEAD, update only submodules whose recorded commit changed, or that are new.</item>
/// <item>Run <c>submodule sync</c> first so URL changes in .gitmodules take effect.</item>
/// <item>Never touch a submodule with uncommitted changes; report it instead.</item>
/// <item>Nested submodules are handled by <c>--recursive</c>.</item>
/// </list>
/// </remarks>
public static class SubmoduleAutoUpdater
{
    /// <summary>Recorded commit per submodule path in the current HEAD (null when not recorded).</summary>
    public static IReadOnlyDictionary<string, string?> Capture(RepositorySnapshot snapshot) =>
        snapshot.Submodules.ToDictionary(s => s.Path, s => s.HeadSha, StringComparer.Ordinal);

    /// <summary>Updates submodules whose recorded commit changed since <paramref name="before"/> was captured.</summary>
    public static async Task<SubmoduleUpdateResult> UpdateChangedAsync(
        IGitRepository repo, IReadOnlyDictionary<string, string?> before, IProgress<string>? progress, CancellationToken ct = default)
    {
        var snapshot = await repo.Read.GetSnapshotAsync(ct).ConfigureAwait(false);
        var targets = new List<string>();
        var skipped = new List<string>();

        foreach (var sm in snapshot.Submodules)
        {
            if (sm.HeadSha is null) continue; // removed from HEAD: nothing to check out
            var isNew = !before.TryGetValue(sm.Path, out var previous);
            var recordedChanged = !isNew && previous != sm.HeadSha;
            if (!isNew && !recordedChanged) continue;
            if (sm.WorkDirSha == sm.HeadSha && !sm.State.HasFlag(SubmoduleState.NotInitialized)) continue; // already there

            if (sm.State.HasFlag(SubmoduleState.WorkDirDirty))
            {
                skipped.Add(sm.Path);
                continue;
            }

            targets.Add(sm.Path);
        }

        await UpdateAsync(repo, targets, progress, ct).ConfigureAwait(false);
        return new SubmoduleUpdateResult(targets, skipped);
    }

    /// <summary>Initializes and checks out (recursively) every submodule that was never initialized. Safe: nothing is checked out yet.</summary>
    public static async Task<SubmoduleUpdateResult> InitializeMissingAsync(IGitRepository repo, IProgress<string>? progress, CancellationToken ct = default)
    {
        var snapshot = await repo.Read.GetSnapshotAsync(ct).ConfigureAwait(false);
        var targets = snapshot.Submodules
            .Where(s => s.HeadSha is not null && s.State.HasFlag(SubmoduleState.NotInitialized))
            .Select(s => s.Path)
            .ToList();
        await UpdateAsync(repo, targets, progress, ct).ConfigureAwait(false);
        return new SubmoduleUpdateResult(targets, []);
    }

    /// <summary>Updates every out-of-sync or uninitialized submodule (explicit user action); dirty ones are skipped.</summary>
    public static async Task<SubmoduleUpdateResult> UpdateAllAsync(IGitRepository repo, IProgress<string>? progress, CancellationToken ct = default)
    {
        var snapshot = await repo.Read.GetSnapshotAsync(ct).ConfigureAwait(false);
        var skipped = snapshot.Submodules.Where(s => s.State.HasFlag(SubmoduleState.WorkDirDirty)).Select(s => s.Path).ToList();
        var targets = snapshot.Submodules.Where(s => s.HeadSha is not null && !skipped.Contains(s.Path)).Select(s => s.Path).ToList();
        await UpdateAsync(repo, targets, progress, ct).ConfigureAwait(false);
        return new SubmoduleUpdateResult(targets, skipped);
    }

    /// <summary>Clean submodules whose checked-out commit differs from the one recorded in HEAD.</summary>
    public static IReadOnlyList<string> OutOfSync(RepositorySnapshot snapshot) =>
        snapshot.Submodules
            .Where(s => s.HeadSha is not null && s.State.HasFlag(SubmoduleState.OutOfSync) && !s.State.HasFlag(SubmoduleState.WorkDirDirty))
            .Select(s => s.Path)
            .ToList();

    private static async Task UpdateAsync(IGitRepository repo, List<string> paths, IProgress<string>? progress, CancellationToken ct)
    {
        if (paths.Count == 0) return;
        await repo.Submodules.SyncAsync(paths, recursive: true, ct).ConfigureAwait(false);
        await repo.Submodules.UpdateAsync(paths, init: true, recursive: true, progress, ct).ConfigureAwait(false);
    }
}
