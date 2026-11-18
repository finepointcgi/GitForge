using System.Net;
using GitForge.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace GitForge.UI.Services;

/// <summary>
/// Dropping one branch on another (sidebar rows or graph labels): asks whether to merge the dragged branch into the
/// target or rebase it onto the target, then runs it. The branch context menus offer the same actions from the keyboard.
/// </summary>
public static class BranchDragDrop
{
    /// <summary>Only local branches accept drops (a merge needs a branch to move; remote-tracking branches never do).</summary>
    public static bool CanDrop(BranchHead? source, BranchHead target) =>
        source is not null && !target.IsRemote && source.CanonicalName != target.CanonicalName;

    public static async Task DropAsync(RepoWorkspace w, IDialogService dialogs, BranchHead target)
    {
        var source = w.DraggedBranch;
        w.EndBranchDrag();
        if (!CanDrop(source, target) || w.IsBusy) return;
        if (w.Snapshot?.Operation is { } op && op != RepositoryOperation.None)
        {
            w.Info($"Finish or abort the {op.ToString().ToLowerInvariant()} in progress first.", Severity.Warning);
            return;
        }

        var (s, t) = (WebUtility.HtmlEncode(source!.Name), WebUtility.HtmlEncode(target.Name));
        var merge = target.IsCurrent
            ? $"<b>Merge</b> brings the commits on {s} into {t}."
            : $"<b>Merge</b> checks out {t}, then brings the commits on {s} into it.";
        var rebase = source.IsCurrent
            ? $"<b>Rebase</b> replays the commits on {s} on top of {t}. This rewrites them."
            : $"<b>Rebase</b> checks out {s}, then replays its commits on top of {t}. This rewrites them.";

        // Remote-tracking branches can be merged but not rebased (there is no local branch to rewrite).
        var title = $"Drop {source.Name} on {target.Name}";
        var choice = source.IsRemote
            ? await dialogs.ShowMessageBoxAsync(title, new MarkupString(merge), yesText: "Merge", cancelText: "Cancel")
            : await dialogs.ShowMessageBoxAsync(title, new MarkupString($"{merge}<br /><br />{rebase}"), yesText: "Merge", noText: "Rebase", cancelText: "Cancel");

        if (choice == true) await w.MergeIntoAsync(source.Name, target);
        else if (choice == false) await w.RebaseBranchAsync(source, target.Name);
    }
}
