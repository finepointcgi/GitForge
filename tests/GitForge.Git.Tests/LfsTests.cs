using GitForge.Git.Engine;

namespace GitForge.Git.Tests;

public class LfsTests
{
    [Fact]
    public async Task TrackedPatternsAndFiles_FromRealRepository()
    {
        await using var t = await TempRepo.CreateAsync();
        await t.GitAsync("lfs", "install", "--local");
        var repo = await t.OpenAsync();
        Assert.True(await repo.Lfs.IsAvailableAsync());

        await repo.Lfs.TrackAsync("*.bin", lockable: false);
        await repo.Lfs.TrackAsync("*.psd", lockable: true);
        t.Write("data.bin", "binary\0payload");
        t.Write("art.psd", "layers");
        t.Write("readme.txt", "plain");
        await t.CommitAllAsync("add assets");

        var patterns = await repo.Lfs.GetPatternsAsync();
        Assert.Contains(patterns, p => p is { Pattern: "*.bin", Lockable: false, Source: ".gitattributes" });
        Assert.Contains(patterns, p => p is { Pattern: "*.psd", Lockable: true });

        var files = await repo.Lfs.GetFilesAsync();
        Assert.Equal(["art.psd", "data.bin"], files.Select(f => f.Path).Order());
        Assert.All(files, f => Assert.Equal(64, f.Oid.Length));

        await Assert.ThrowsAsync<ArgumentException>(() => repo.Lfs.TrackAsync("--everything", false));
    }

    [Fact]
    public void ParsesVerifiedLocks()
    {
        const string json = """
            {"ours":[{"id":"7","path":"art/hero.psd","owner":{"name":"Alice"},"locked_at":"2026-11-17T10:00:00Z"}],
             "theirs":[{"id":"9","path":"cad/frame.sldprt","owner":{"name":"Bob"},"locked_at":"2026-11-16T08:30:00Z"}]}
            """;
        var list = LfsOperations.ParseVerifiedLocks(json);

        Assert.True(list.OwnershipKnown);
        Assert.Equal(2, list.Locks.Count);
        var ours = list.Locks.Single(l => l.Path == "art/hero.psd");
        Assert.Equal(("7", "Alice", true), (ours.Id, ours.Owner, ours.IsOurs));
        Assert.False(list.Locks.Single(l => l.Owner == "Bob").IsOurs);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.Zero), list.Locks.Single(l => l.Owner == "Bob").LockedAt);
    }

    [Fact]
    public void ParsesPlainLockArray_AndEmptyOutput()
    {
        var locks = LfsOperations.ParseLockArray("""[{"id":"1","path":"a.bin","owner":{"name":"Jane Doe"},"locked_at":"2016-05-17T15:49:06+00:00"}]""", null);
        Assert.Equal(("a.bin", "Jane Doe", (bool?)null), (locks[0].Path, locks[0].Owner, locks[0].IsOurs));
        Assert.Empty(LfsOperations.ParseLockArray("", null));
        Assert.Empty(LfsOperations.ParseVerifiedLocks("").Locks);
    }

    [Fact]
    public void ParsesTrackListing()
    {
        const string output = "Listing tracked patterns\n    *.bin (.gitattributes)\n    assets/*.psd [lockable] (assets/.gitattributes)\nListing excluded patterns\n    *.tmp (.gitattributes)\n";
        var patterns = LfsOperations.ParsePatterns(output);
        Assert.Equal(2, patterns.Count);
        Assert.Equal(("assets/*.psd", true, "assets/.gitattributes"), (patterns[1].Pattern, patterns[1].Lockable, patterns[1].Source));
    }
}
