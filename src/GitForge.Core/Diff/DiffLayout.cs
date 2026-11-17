namespace GitForge.Core.Diff;

/// <summary>Flattens hunks into fixed-height rows for virtualized rendering (10k+ line diffs stay at a constant DOM size).</summary>
public static class DiffLayout
{
    public static IReadOnlyList<DiffRow> Inline(FileDiff diff)
    {
        var rows = new List<DiffRow>(diff.LineCount);
        foreach (var hunk in diff.Hunks)
        {
            rows.Add(new DiffRow(rows.Count, hunk.Index, -1, hunk, null));
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                rows.Add(new DiffRow(rows.Count, hunk.Index, i, hunk, hunk.Lines[i]));
            }
        }

        return rows;
    }

    /// <summary>Aligns each run of deletions with the run of additions that follows it.</summary>
    public static IReadOnlyList<SideBySideRow> SideBySide(FileDiff diff)
    {
        var rows = new List<SideBySideRow>(diff.LineCount);
        foreach (var hunk in diff.Hunks)
        {
            rows.Add(new SideBySideRow(rows.Count, hunk.Index, hunk, true, null, -1, null, -1));
            var lines = hunk.Lines;
            var i = 0;
            while (i < lines.Count)
            {
                if (lines[i].Kind == DiffLineKind.Context)
                {
                    rows.Add(new SideBySideRow(rows.Count, hunk.Index, hunk, false, lines[i], i, lines[i], i));
                    i++;
                    continue;
                }

                var dels = new List<int>();
                var adds = new List<int>();
                while (i < lines.Count && lines[i].Kind == DiffLineKind.Deleted) dels.Add(i++);
                while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) adds.Add(i++);

                var n = Math.Max(dels.Count, adds.Count);
                for (var k = 0; k < n; k++)
                {
                    var l = k < dels.Count ? dels[k] : -1;
                    var r = k < adds.Count ? adds[k] : -1;
                    rows.Add(new SideBySideRow(
                        rows.Count, hunk.Index, hunk, false,
                        l >= 0 ? lines[l] : null, l,
                        r >= 0 ? lines[r] : null, r));
                }
            }
        }

        return rows;
    }
}
