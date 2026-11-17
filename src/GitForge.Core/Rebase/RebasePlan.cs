using System.Text;

namespace GitForge.Core.Rebase;

public enum RebaseAction
{
    Pick,
    Reword,
    Edit,
    Squash,
    Fixup,
    Drop,
}

/// <summary>One row on the interactive-rebase canvas, oldest commit first (git todo order).</summary>
public sealed record RebaseTodoItem(string Sha, string Subject, RebaseAction Action = RebaseAction.Pick, string? NewMessage = null)
{
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
}

/// <summary>Serializes a visual plan into a <c>git-rebase-todo</c> file.</summary>
public static class RebaseTodoWriter
{
    /// <summary>Returns <c>null</c> when valid; otherwise a user-facing error.</summary>
    public static string? Validate(IReadOnlyList<RebaseTodoItem> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count == 0) return "The rebase plan is empty.";

        var firstKept = plan.FirstOrDefault(p => p.Action != RebaseAction.Drop);
        if (firstKept is null) return "Every commit is marked Drop. Use Reset instead if that is intended.";
        if (firstKept.Action is RebaseAction.Squash or RebaseAction.Fixup)
        {
            return "The first kept commit cannot be squash or fixup: there is no earlier commit to combine with.";
        }

        if (plan.Any(p => p.Action == RebaseAction.Reword && string.IsNullOrWhiteSpace(p.NewMessage)))
        {
            return "Reword requires a new commit message.";
        }

        return null;
    }

    /// <param name="messageFileFor">Writes a reword message to disk and returns its absolute path.</param>
    public static string Write(IReadOnlyList<RebaseTodoItem> plan, Func<RebaseTodoItem, string> messageFileFor)
    {
        var error = Validate(plan);
        if (error is not null) throw new InvalidOperationException(error);

        var sb = new StringBuilder();
        foreach (var item in plan)
        {
            if (!IsHex(item.Sha)) throw new InvalidOperationException($"Invalid commit id '{item.Sha}'.");
            switch (item.Action)
            {
                case RebaseAction.Reword:
                    // Non-interactive reword: pick, then amend the message from a file (no editor round-trip).
                    sb.Append("pick ").Append(item.Sha).Append('\n');
                    sb.Append("exec git commit --amend --only --allow-empty -F ")
                      .Append(ShellQuote(messageFileFor(item))).Append('\n');
                    break;
                default:
                    sb.Append(item.Action.ToString().ToLowerInvariant()).Append(' ').Append(item.Sha).Append('\n');
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Single-quotes a path for the POSIX shell git uses to run <c>exec</c> lines.</summary>
    public static string ShellQuote(string path)
    {
        var p = path.Replace('\\', '/');
        return "'" + p.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsHex(string s) => s.Length is >= 7 and <= 64 && s.All(Uri.IsHexDigit);
}
