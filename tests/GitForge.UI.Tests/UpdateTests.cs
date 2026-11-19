using GitForge.Core.Abstractions;
using GitForge.UI.Services;

namespace GitForge.UI.Tests;

public class UpdateFeedTests
{
    private const string Default = "https://github.com/finepointcgi/GitForge";

    [Theory]
    [InlineData(null, UpdateFeedKind.GitHub, "https://github.com/finepointcgi/GitForge")]
    [InlineData("  ", UpdateFeedKind.GitHub, "https://github.com/finepointcgi/GitForge")]
    [InlineData("https://github.com/acme/tools.git/", UpdateFeedKind.GitHub, "https://github.com/acme/tools")]
    [InlineData("https://updates.example.com/gitforge/", UpdateFeedKind.Web, "https://updates.example.com/gitforge")]
    [InlineData("http://127.0.0.1:8080/feed", UpdateFeedKind.Web, "http://127.0.0.1:8080/feed")]
    [InlineData(@"\\server\share\gitforge", UpdateFeedKind.Folder, @"\\server\share\gitforge")]
    [InlineData(@"C:\feeds\gitforge", UpdateFeedKind.Folder, @"C:\feeds\gitforge")]
    public void TryParse_AcceptsSupportedFeeds(string? value, UpdateFeedKind kind, string location)
    {
        Assert.True(UpdateFeed.TryParse(value, Default, out var feed, out var error), error);
        Assert.Equal(new UpdateFeed(kind, location), feed);
    }

    [Theory]
    [InlineData("http://updates.example.com/gitforge")] // plain http could be tampered with in transit
    [InlineData("ftp://updates.example.com/gitforge")]
    [InlineData("https://github.com/acme")]               // not a repository
    [InlineData(@"feeds\gitforge")]                        // relative path
    public void TryParse_RejectsUnsafeOrAmbiguousFeeds(string value)
    {
        Assert.False(UpdateFeed.TryParse(value, Default, out var feed, out var error));
        Assert.Null(feed);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Describe_NamesTheGitHubRepository() =>
        Assert.Equal("GitHub releases (acme/tools)", new UpdateFeed(UpdateFeedKind.GitHub, "https://github.com/acme/tools").Describe());
}

public class UpdateServiceTests
{
    private static (UpdateService Service, FakeUpdater Updater, FakeSettings Settings) Create(
        AppSettings? settings = null, UpdatePolicy? policy = null, string? unavailable = null)
    {
        var updater = new FakeUpdater { Policy = policy ?? UpdatePolicy.None, UnavailableReason = unavailable };
        var store = new FakeSettings(settings ?? new AppSettings());
        return (new UpdateService(updater, store), updater, store);
    }

    [Fact]
    public async Task Check_FindsAnUpdate()
    {
        var (service, updater, _) = Create();
        updater.Next = new AppUpdate("0.2.0", "notes");

        var update = await service.CheckAsync();

        Assert.Equal("0.2.0", update?.Version);
        Assert.Equal(UpdateStatus.Available, service.Status);
        Assert.Equal(new UpdateFeed(UpdateFeedKind.GitHub, FakeUpdater.Repo), updater.CheckedFeed);
    }

    [Fact]
    public async Task Check_UpToDate()
    {
        var (service, _, _) = Create();
        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStatus.UpToDate, service.Status);
    }

    [Fact]
    public async Task Check_Failure_ReportsTheFeed()
    {
        var (service, updater, _) = Create();
        updater.CheckError = new HttpRequestException("No such host is known.");

        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStatus.Failed, service.Status);
        Assert.Contains("GitHub releases (finepointcgi/GitForge)", service.Error, StringComparison.Ordinal);
        Assert.Contains("No such host", service.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_WhileAnotherCheckRuns_DoesNotSendASecondRequest()
    {
        var (service, updater, _) = Create();
        var response = new TaskCompletionSource<AppUpdate?>();
        updater.Pending = response.Task;

        var first = service.CheckAsync();
        Assert.Null(await service.CheckAsync()); // returns at once with what is known so far
        response.SetResult(new AppUpdate("0.2.0", null));

        Assert.Equal("0.2.0", (await first)?.Version);
        Assert.Equal(1, updater.CheckCount);
    }

    [Fact]
    public async Task Check_PolicyFeedOverridesTheUsersFeed()
    {
        var (service, updater, _) = Create(new AppSettings { UpdateFeed = "https://user.example.com/feed" }, new UpdatePolicy(false, @"\\it\share\gitforge"));
        await service.CheckAsync();
        Assert.Equal(new UpdateFeed(UpdateFeedKind.Folder, @"\\it\share\gitforge"), updater.CheckedFeed);
    }

    [Fact]
    public async Task Check_InvalidFeed_FailsWithoutANetworkRequest()
    {
        var (service, updater, _) = Create(new AppSettings { UpdateFeed = "http://updates.example.com" });
        await service.CheckAsync();
        Assert.Equal(UpdateStatus.Failed, service.Status);
        Assert.Null(updater.CheckedFeed);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "Not installed.")]
    public async Task Check_DisabledByPolicyOrNotInstalled_MakesNoRequest(bool policyDisabled, string? unavailable)
    {
        var (service, updater, _) = Create(policy: new UpdatePolicy(policyDisabled, null), unavailable: unavailable);
        Assert.False(service.CanUpdate);
        Assert.Null(await service.CheckAsync());
        Assert.Null(updater.CheckedFeed);
    }

    [Fact]
    public async Task Install_DownloadsThenRestarts()
    {
        var (service, updater, _) = Create();
        updater.Next = new AppUpdate("0.2.0", null);
        await service.CheckAsync();

        Assert.True(await service.InstallAsync());
        Assert.Equal(["download 0.2.0", "apply 0.2.0"], updater.Calls);
    }

    [Fact]
    public async Task Install_DownloadFailure_DoesNotRestart_AndCanRetry()
    {
        var (service, updater, _) = Create();
        updater.Next = new AppUpdate("0.2.0", null);
        await service.CheckAsync();
        updater.DownloadError = new IOException("Checksum mismatch.");

        Assert.False(await service.InstallAsync());
        Assert.Equal(UpdateStatus.Failed, service.Status);
        Assert.Contains("Checksum mismatch", service.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("apply 0.2.0", updater.Calls);

        updater.DownloadError = null;
        Assert.True(await service.InstallAsync()); // the update is still known, so "Try again" re-downloads
        Assert.Contains("apply 0.2.0", updater.Calls);
    }

    [Fact]
    public void AutomaticCheck_RunsAtMostDaily_AndOnlyWhenEnabled()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(Create().Service.IsAutomaticCheckDue(now));
        Assert.True(Create(new AppSettings { LastUpdateCheck = now - UpdateService.CheckInterval }).Service.IsAutomaticCheckDue(now));
        Assert.False(Create(new AppSettings { LastUpdateCheck = now.AddHours(-23) }).Service.IsAutomaticCheckDue(now));
        Assert.False(Create(new AppSettings { CheckForUpdates = false }).Service.IsAutomaticCheckDue(now));
        Assert.False(Create(policy: new UpdatePolicy(true, null)).Service.IsAutomaticCheckDue(now));
    }

    [Fact]
    public async Task AutomaticCheck_RecordsTheTime_UnlessAnUpdateWasFound()
    {
        var now = DateTimeOffset.UtcNow;
        var (upToDate, _, settings) = Create();
        await upToDate.RunAutomaticCheckAsync(now, default);
        Assert.Equal(now, settings.Current.LastUpdateCheck);

        var (found, updater, foundSettings) = Create();
        updater.Next = new AppUpdate("0.2.0", null);
        AppUpdate? announced = null;
        found.UpdateFound += u => announced = u;
        await found.RunAutomaticCheckAsync(now, default);
        Assert.Equal("0.2.0", announced?.Version);
        Assert.Null(foundSettings.Current.LastUpdateCheck); // checked again next start, so the notice comes back
        Assert.False(found.IsAutomaticCheckDue(now));       // and not re-checked while the update waits
    }

    [Fact]
    public async Task AutomaticCheck_ThatFails_IsRetriedOnTheNextPass()
    {
        var now = DateTimeOffset.UtcNow;
        var (service, updater, settings) = Create();
        updater.CheckError = new HttpRequestException("No such host is known.");

        await service.RunAutomaticCheckAsync(now, default);
        Assert.Equal(UpdateStatus.Failed, service.Status);
        Assert.Null(settings.Current.LastUpdateCheck); // offline at launch must not skip a day of checks
        Assert.True(service.IsAutomaticCheckDue(now.AddHours(1)));

        updater.CheckError = null;
        await service.RunAutomaticCheckAsync(now.AddHours(1), default);
        Assert.Equal(now.AddHours(1), settings.Current.LastUpdateCheck);
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public const string Repo = "https://github.com/finepointcgi/GitForge";

        public AppUpdate? Next { get; set; }
        public Exception? CheckError { get; set; }
        public Exception? DownloadError { get; set; }
        public Task<AppUpdate?>? Pending { get; set; }
        public int CheckCount { get; private set; }
        public UpdateFeed? CheckedFeed { get; private set; }
        public List<string> Calls { get; } = [];

        public string CurrentVersion => "0.1.0";
        public string? UnavailableReason { get; set; }
        public UpdatePolicy Policy { get; set; } = UpdatePolicy.None;
        public string DefaultFeed => Repo;

        public Task<AppUpdate?> CheckAsync(UpdateFeed feed, CancellationToken cancellationToken = default)
        {
            CheckedFeed = feed;
            CheckCount++;
            if (Pending is { } pending) return pending;
            return CheckError is { } e ? Task.FromException<AppUpdate?>(e) : Task.FromResult(Next);
        }

        public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken = default)
        {
            Calls.Add($"download {update.Version}");
            if (DownloadError is { } e) return Task.FromException(e);
            progress(100);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(AppUpdate update) => Calls.Add($"apply {update.Version}");
    }

    private sealed class FakeSettings(AppSettings current) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = current;
        public event EventHandler<AppSettings>? Changed;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, settings);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) => SaveAsync(update(Current), cancellationToken);
    }
}
