using GitForge.Core.Models;
using GitForge.Git.Undo;
using GitForge.UI.Dialogs;
using MudBlazor;

namespace GitForge.UI.Services;

/// <summary>Commit actions shared by the graph's right-click menu and the command palette (which acts on the selected commit).</summary>
public static class CommitActions
{
    public static async Task NewBranchAsync(RepoWorkspace w, IDialogService dialogs, CommitInfo c)
    {
        var name = await TextPromptDialog.ShowAsync(dialogs, $"New branch at {c.ShortSha}", "Branch name", string.Empty, validateRef: true);
        if (name is not null) await w.RunUndoableAsync($"Creating {name}", r => UndoableOperations.CreateBranchAsync(r, name, c.Sha, checkout: true), $"Created and checked out {name}");
    }

    public static async Task NewTagAsync(RepoWorkspace w, IDialogService dialogs, CommitInfo c)
    {
        var name = await TextPromptDialog.ShowAsync(dialogs, $"New tag at {c.ShortSha}", "Tag name", string.Empty, validateRef: true);
        if (name is null) return;
        var message = await TextPromptDialog.ShowAsync(dialogs, "Tag message", "Message (leave empty for a lightweight tag)", string.Empty, allowEmpty: true, multiline: true);
        if (message is null) return;
        await w.RunAsync($"Tagging {name}", (r, _) => r.Branches.CreateTagAsync(name, c.Sha, message), $"Created tag {name}");
    }

    public static Task CherryPickAsync(RepoWorkspace w, CommitInfo c) =>
        c.IsMerge
            ? Warn(w, $"{c.ShortSha} is a merge commit. Cherry-pick the commits it merged instead.")
            : w.RunAsync($"Cherry-picking {c.ShortSha}", (r, _) => r.History.CherryPickAsync([c.Sha]), $"Cherry-picked {c.ShortSha} onto {w.CurrentBranch ?? "HEAD"}");

    public static async Task RevertAsync(RepoWorkspace w, IDialogService dialogs, CommitInfo c)
    {
        if (await dialogs.ShowMessageBoxAsync("Revert commit", $"Create a new commit that reverts {c.ShortSha} \"{c.Subject}\"?", yesText: "Revert", cancelText: "Cancel") == true)
        {
            await w.RunAsync($"Reverting {c.ShortSha}", (r, _) => r.History.RevertAsync(c.Sha));
        }
    }

    public static async Task ResetAsync(RepoWorkspace w, IDialogService dialogs, CommitInfo c, ResetMode mode)
    {
        if (mode == ResetMode.Hard &&
            await dialogs.ShowMessageBoxAsync("Hard reset", $"Move {w.CurrentBranch ?? "HEAD"} to {c.ShortSha} and DISCARD all uncommitted changes? Committed work stays recoverable via the reflog.", yesText: "Reset hard", cancelText: "Cancel") != true)
        {
            return;
        }

        await w.RunUndoableAsync($"Resetting to {c.ShortSha}", r => UndoableOperations.ResetAsync(r, c.Sha, mode), $"Reset complete ({Keymap.UndoName(w.Settings)} reverses it)");
    }

    public static async Task InteractiveRebaseAsync(RepoWorkspace w, IDialogService dialogs, CommitInfo c)
    {
        if (!w.Status.IsClean)
        {
            w.Info("Commit or stash your changes before an interactive rebase.", Severity.Warning);
            return;
        }

        var parameters = new DialogParameters<InteractiveRebaseDialog> { { d => d.BaseSha, c.Sha } };
        var dialog = await dialogs.ShowAsync<InteractiveRebaseDialog>($"Interactive rebase onto {c.ShortSha}", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseButton = true });
        await dialog.Result;
    }

    /// <summary>Saves commits (oldest first) as .patch files in a folder the user picks.</summary>
    public static async Task SavePatchesAsync(RepoWorkspace w, IPlatformShell shell, IDialogService dialogs, IReadOnlyList<CommitInfo> commitsOldestFirst)
    {
        if (commitsOldestFirst.Count == 0) return;
        var folder = await PathPrompt.FolderAsync(shell, dialogs, $"Save {(commitsOldestFirst.Count == 1 ? "1 patch" : $"{commitsOldestFirst.Count} patches")} to folder", null);
        if (folder is not null) await w.SaveCommitsAsPatchesAsync(commitsOldestFirst, folder);
    }

    /// <summary>Starts a bisect with this commit, or marks it during one.</summary>
    public static Task BisectAsync(RepoWorkspace w, CommitInfo c, BisectMark mark) =>
        w.IsBisecting ? w.MarkBisectAsync(mark, c.Sha) : w.StartBisectAsync(c.Sha, mark);

    private static Task Warn(RepoWorkspace w, string message)
    {
        w.Info(message, Severity.Warning);
        return Task.CompletedTask;
    }
}

/// <summary>Asks for a folder or file: the host's native dialog when it has one, otherwise a typed path.</summary>
public static class PathPrompt
{
    public const string PatchFilter = "Patch files (*.patch;*.diff)|*.patch;*.diff|All files (*.*)|*.*";
    public const string BundleFilter = "Git bundles (*.bundle)|*.bundle|All files (*.*)|*.*";

    public static async Task<string?> FolderAsync(IPlatformShell shell, IDialogService dialogs, string title, string? initial) =>
        shell.CanPickFolder
            ? await shell.PickFolderAsync(initial)
            : await TextPromptDialog.ShowAsync(dialogs, title, "Folder (full path)", initial ?? string.Empty);

    public static async Task<string?> OpenFileAsync(IPlatformShell shell, IDialogService dialogs, string title, string filter, string? initialDirectory = null) =>
        shell.CanPickFiles
            ? await shell.PickOpenFileAsync(title, filter, initialDirectory)
            : await TextPromptDialog.ShowAsync(dialogs, title, "File (full path)", string.Empty);

    public static async Task<string?> SaveFileAsync(IPlatformShell shell, IDialogService dialogs, string title, string fileName, string filter, string? initialDirectory = null) =>
        shell.CanPickFiles
            ? await shell.PickSaveFileAsync(title, fileName, filter, initialDirectory)
            : await TextPromptDialog.ShowAsync(dialogs, title, "File (full path)", initialDirectory is null ? fileName : Path.Combine(initialDirectory, fileName));
}
