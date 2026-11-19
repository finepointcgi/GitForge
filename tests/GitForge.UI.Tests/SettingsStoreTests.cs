using GitForge.Core.Abstractions;
using GitForge.Platform.Settings;

namespace GitForge.UI.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gitforge-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ConcurrentUpdates_AreAllKept()
    {
        using var store = new JsonSettingsStore(_path);
        await store.LoadAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => store.UpdateAsync(s => s with { RecentRepositories = [.. s.RecentRepositories, $"repo{i}"] }))));

        Assert.Equal(20, store.Current.RecentRepositories.Distinct().Count());
        using var reloaded = new JsonSettingsStore(_path);
        Assert.Equal(20, (await reloaded.LoadAsync()).RecentRepositories.Count);
    }

    [Fact]
    public async Task Save_WaitsOutABriefLockOnTheFile()
    {
        using var store = new JsonSettingsStore(_path);
        await store.SaveAsync(new AppSettings { AutoFetchMinutes = 1 });

        // Like an antivirus scan of the last write: the file is open without delete sharing, so it can't be replaced yet.
        var scan = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var save = store.UpdateAsync(s => s with { AutoFetchMinutes = 2 });
        await Task.Delay(80);
        await scan.DisposeAsync();
        await save;

        using var reloaded = new JsonSettingsStore(_path);
        Assert.Equal(2, (await reloaded.LoadAsync()).AutoFetchMinutes);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _path, _path + ".tmp" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
