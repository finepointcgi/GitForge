using System.Text;

namespace GitForge.Core.Diff;

/// <summary>
/// Builds partial patches for hunk- and line-level staging, unstaging, and discarding.
/// The resulting text is fed to <c>git apply</c> on standard input.
/// </summary>
/// <remarks>
/// Forward patches (<see cref="PatchAction.Stage"/>) must match the pre-image (old side):
/// unselected deletions become context and unselected additions are dropped.
/// Reverse patches (<see cref="PatchAction.Unstage"/>, <see cref="PatchAction.Discard"/>) must match the post-image (new side):
/// unselected additions become context and unselected deletions are dropped.
/// </remarks>
public static class PatchBuilder
{
    public static bool IsReverse(PatchAction action) => action is PatchAction.Unstage or PatchAction.Discard;

    /// <summary>Returns the patch text, or <c>null</c> when the selection contains no changed lines.</summary>
    public static string? Build(FileDiff diff, IReadOnlySet<LineRef> selection, PatchAction action)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(selection);
        if (!diff.SupportsPartialStaging)
        {
            throw new InvalidOperationException($"Partial patches are not supported for '{diff.Path}' (binary, new, deleted, or renamed file).");
        }

        var reverse = IsReverse(action);
        var sb = new StringBuilder();
        var delta = 0;
        var emitted = 0;

        foreach (var hunk in diff.Hunks)
        {
            var body = new List<(char Prefix, DiffLine Line)>(hunk.Lines.Count);
            var hasSelected = false;

            for (var li = 0; li < hunk.Lines.Count; li++)
            {
                var line = hunk.Lines[li];
                var selected = line.IsChange && selection.Contains(new LineRef(hunk.Index, li));
                hasSelected |= selected;

                switch (line.Kind)
                {
                    case DiffLineKind.Context:
                        body.Add((' ', line));
                        break;
                    case DiffLineKind.Added when selected:
                        body.Add(('+', line));
                        break;
                    case DiffLineKind.Added when reverse:
                        body.Add((' ', line));
                        break;
                    case DiffLineKind.Deleted when selected:
                        body.Add(('-', line));
                        break;
                    case DiffLineKind.Deleted when !reverse:
                        body.Add((' ', line));
                        break;
                }
            }

            if (!hasSelected) continue;

            var oldCount = body.Count(b => b.Prefix is ' ' or '-');
            var newCount = body.Count(b => b.Prefix is ' ' or '+');
            int oldStart, newStart;
            if (reverse)
            {
                newStart = hunk.NewStart;
                oldStart = AdjustStart(hunk.NewStart, delta, oldCount);
                delta += oldCount - newCount;
            }
            else
            {
                oldStart = hunk.OldStart;
                newStart = AdjustStart(hunk.OldStart, delta, newCount);
                delta += newCount - oldCount;
            }

            if (emitted++ == 0)
            {
                foreach (var h in diff.HeaderLines) sb.Append(h).Append('\n');
            }

            sb.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
              .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");

            foreach (var (prefix, line) in body)
            {
                sb.Append(prefix).Append(line.Text).Append('\n');
                if (line.NoNewlineAtEnd) sb.Append("\\ No newline at end of file\n");
            }
        }

        return emitted == 0 ? null : sb.ToString();
    }

    /// <summary>Selects every changed line in a hunk.</summary>
    public static HashSet<LineRef> SelectHunk(DiffHunk hunk)
    {
        var set = new HashSet<LineRef>();
        for (var i = 0; i < hunk.Lines.Count; i++)
        {
            if (hunk.Lines[i].IsChange) set.Add(new LineRef(hunk.Index, i));
        }

        return set;
    }

    // Unified diff convention: an empty side starts at the line *before* the hunk.
    private static int AdjustStart(int baseStart, int delta, int count)
    {
        var start = baseStart + delta;
        if (count == 0 && start > 0) start--;
        return Math.Max(start, 0);
    }
}
