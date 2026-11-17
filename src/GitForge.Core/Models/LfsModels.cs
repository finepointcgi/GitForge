namespace GitForge.Core.Models;

/// <summary>A .gitattributes pattern routed through Git LFS.</summary>
public sealed record LfsPattern(string Pattern, bool Lockable, string Source);

/// <summary>A file stored in LFS (from <c>git lfs ls-files</c>). <see cref="Downloaded"/> is false for un-smudged pointers.</summary>
public sealed record LfsFile(string Path, long Size, string Oid, bool Downloaded);

public sealed record LfsLock(string Id, string Path, string Owner, DateTimeOffset? LockedAt, bool? IsOurs);

/// <summary>Locks on the LFS server. <see cref="Error"/> is set when the server could not be reached or does not support locking.</summary>
public sealed record LfsLockList(IReadOnlyList<LfsLock> Locks, bool OwnershipKnown, string? Error)
{
    public static readonly LfsLockList Empty = new([], false, null);
}

/// <summary>Everything the UI shows about LFS for one repository.</summary>
public sealed record LfsState(bool Available, IReadOnlyList<LfsPattern> Patterns, IReadOnlyList<LfsFile> Files, LfsLockList Locks)
{
    public static readonly LfsState None = new(false, [], [], LfsLockList.Empty);
    public bool InUse => Patterns.Count > 0 || Files.Count > 0;
    public bool HasLockablePatterns => Patterns.Any(p => p.Lockable);
}
