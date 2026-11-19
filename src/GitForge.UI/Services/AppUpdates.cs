using System.Diagnostics.CodeAnalysis;
using GitForge.Core.Abstractions;

namespace GitForge.UI.Services;

public enum UpdateFeedKind
{
    /// <summary>Releases of a GitHub repository.</summary>
    GitHub,

    /// <summary>A static https site holding release files.</summary>
    Web,

    /// <summary>A local folder or UNC share holding release files (offline and air-gapped networks).</summary>
    Folder,
}

/// <summary>Where installed copies look for new releases.</summary>
public sealed record UpdateFeed(UpdateFeedKind Kind, string Location)
{
    /// <summary>
    /// Parses a feed from settings or policy. Empty means <paramref name="defaultFeed"/>. Accepts https URLs (a
    /// github.com/owner/repo URL means that repository's releases), http only for localhost, and full folder or UNC paths.
    /// </summary>
    public static bool TryParse(string? value, string defaultFeed, [NotNullWhen(true)] out UpdateFeed? feed, out string? error)
    {
        feed = null;
        error = null;
        var text = string.IsNullOrWhiteSpace(value) ? defaultFeed.Trim() : value.Trim();
        if (text.Length == 0)
        {
            error = "No update feed is configured.";
            return false;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                error = "Use an https:// URL or a full folder path.";
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            {
                error = "Update feeds must use https (http is allowed only for localhost).";
                return false;
            }

            if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    error = "A GitHub feed must name a repository: https://github.com/owner/repo.";
                    return false;
                }

                var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
                feed = new UpdateFeed(UpdateFeedKind.GitHub, $"https://github.com/{parts[0]}/{repo}");
                return true;
            }

            feed = new UpdateFeed(UpdateFeedKind.Web, uri.AbsoluteUri.TrimEnd('/'));
            return true;
        }

        if (!Path.IsPathFullyQualified(text))
        {
            error = "Use an https:// URL or a full folder path (C:\\… or \\\\server\\share\\…).";
            return false;
        }

        feed = new UpdateFeed(UpdateFeedKind.Folder, text);
        return true;
    }

    /// <summary>"GitHub releases (owner/repo)", the URL, or the folder.</summary>
    public string Describe() => Kind == UpdateFeedKind.GitHub
        ? $"GitHub releases ({Location["https://github.com/".Length..]})"
        : Location;
}

/// <summary>Machine policy set by IT (HKLM or HKCU\SOFTWARE\Policies\GitForge): turn updates off, or pin the feed.</summary>
public sealed record UpdatePolicy(bool Disabled, string? Feed)
{
    public static readonly UpdatePolicy None = new(false, null);

    public bool IsManaged => Disabled || Feed is not null;
}

/// <summary>A newer release offered by the feed. <see cref="Notes"/> is Markdown, shown as plain text.</summary>
public sealed record AppUpdate(string Version, string? Notes);

/// <summary>Host updater: Velopack in the installed desktop app; none in the dev host or a copy run from source.</summary>
public interface IAppUpdater
{
    /// <summary>The running version, e.g. "0.2.0".</summary>
    string CurrentVersion { get; }

    /// <summary>Null when this copy can update itself; otherwise why it can't.</summary>
    string? UnavailableReason { get; }

    UpdatePolicy Policy { get; }

    /// <summary>The feed this build was published with (used when neither policy nor settings name one).</summary>
    string DefaultFeed { get; }

    /// <summary>Returns the newest release when it is newer than this one; null when up to date.</summary>
    Task<AppUpdate?> CheckAsync(UpdateFeed feed, CancellationToken cancellationToken = default);

    /// <summary>Downloads and verifies (SHA-256) the release found by the last <see cref="CheckAsync"/>.</summary>
    Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken = default);

    /// <summary>Exits GitForge, installs the downloaded release, and starts the new version.</summary>
    void ApplyAndRestart(AppUpdate update);
}

/// <summary>Updater for hosts that can't update themselves.</summary>
public sealed class NoAppUpdater : IAppUpdater
{
    public string CurrentVersion { get; } = AppVersion.Of(typeof(NoAppUpdater));
    public string? UnavailableReason => "Updates apply to the installed desktop app.";
    public UpdatePolicy Policy => UpdatePolicy.None;
    public string DefaultFeed => string.Empty;
    public Task<AppUpdate?> CheckAsync(UpdateFeed feed, CancellationToken cancellationToken = default) => Task.FromResult<AppUpdate?>(null);
    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void ApplyAndRestart(AppUpdate update) { }
}

public static class AppVersion
{
    /// <summary>The assembly's product version without the "+commit" suffix the SDK appends.</summary>
    public static string Of(Type type)
    {
        var info = type.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return info is null ? type.Assembly.GetName().Version?.ToString(3) ?? "0.0.0" : info.Split('+')[0];
    }
}

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Failed,
}

/// <summary>
/// Update checks and installs for the UI. Automatic checks run at most once a day, send nothing but the feed request,
/// and never install without the user's go-ahead.
/// </summary>
public sealed class UpdateService(IAppUpdater updater, ISettingsStore settings) : IDisposable
{
    internal static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1); // one check or download at a time (background loop vs. the user)
    private Task? _loop;

    /// <summary>Status, progress, or the available update changed (raised on background threads).</summary>
    public event Action? Changed;

    /// <summary>An automatic check found a new release.</summary>
    public event Action<AppUpdate>? UpdateFound;

    public UpdateStatus Status { get; private set; }
    public AppUpdate? Available { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }

    public string CurrentVersion => updater.CurrentVersion;
    public string? UnavailableReason => updater.UnavailableReason;
    public UpdatePolicy Policy => updater.Policy;

    /// <summary>This copy can update itself and policy allows it.</summary>
    public bool CanUpdate => updater.UnavailableReason is null && !updater.Policy.Disabled;

    /// <summary>The feed in effect: policy first, then the user's setting, then the build's default.</summary>
    public bool TryGetFeed([NotNullWhen(true)] out UpdateFeed? feed, out string? error) =>
        UpdateFeed.TryParse(Policy.Feed ?? settings.Current.UpdateFeed, updater.DefaultFeed, out feed, out error);

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUpdate || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Available;
        try
        {
            return await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppUpdate?> CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (!TryGetFeed(out var feed, out var feedError))
        {
            Fail(feedError);
            return null;
        }

        Error = null;
        Set(UpdateStatus.Checking);
        try
        {
            var update = await updater.CheckAsync(feed, cancellationToken).ConfigureAwait(false);
            Available = update;
            Set(update is null ? UpdateStatus.UpToDate : UpdateStatus.Available);
            return update;
        }
        catch (OperationCanceledException)
        {
            Set(UpdateStatus.Idle);
            throw;
        }
        catch (Exception ex)
        {
            Available = null; // a failed download keeps Available, so "Try again" knows what to retry
            Fail($"Couldn't reach {feed.Describe()}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the available update, then restarts into it. Returns false (with <see cref="Error"/>) on failure.</summary>
    public async Task<bool> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (Available is null || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            return await InstallCoreAsync(Available, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> InstallCoreAsync(AppUpdate update, CancellationToken cancellationToken)
    {
        Progress = 0;
        Error = null;
        Set(UpdateStatus.Downloading);
        try
        {
            await updater.DownloadAsync(update, p =>
            {
                Progress = Math.Clamp(p, 0, 100);
                Changed?.Invoke();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail($"Couldn't download GitForge {update.Version}: {ex.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            Set(UpdateStatus.Available);
            throw;
        }

        updater.ApplyAndRestart(update); // exits the process
        return true;
    }

    /// <summary>Starts the background check loop once (not at all when this copy can't update); each pass re-reads settings.</summary>
    public void StartAutomaticChecks()
    {
        if (CanUpdate) _loop ??= RunAsync(_stop.Token);
    }

    /// <summary>Automatic checks are on, allowed, and the last one is a day old (or never ran).</summary>
    internal bool IsAutomaticCheckDue(DateTimeOffset now) =>
        CanUpdate
        && settings.Current.CheckForUpdates
        && Status is UpdateStatus.Idle or UpdateStatus.UpToDate or UpdateStatus.Failed
        && (settings.Current.LastUpdateCheck is not { } last || now - last >= CheckInterval);

    /// <summary>
    /// One automatic pass. Records the time only when the feed answered "up to date": a found update should be announced
    /// again after a restart, and a failed check (offline, VPN not connected yet) is retried on the next hourly pass.
    /// </summary>
    internal async Task RunAutomaticCheckAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!IsAutomaticCheckDue(now)) return;
        var update = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (update is not null)
        {
            UpdateFound?.Invoke(update);
            return;
        }

        if (Status == UpdateStatus.UpToDate) await settings.UpdateAsync(s => s with { LastUpdateCheck = now }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunAutomaticCheckAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fail(ex.Message); // e.g. the settings file is locked; try again next pass
                }

                await Task.Delay(LoopInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void Fail(string? message)
    {
        Error = message;
        Set(UpdateStatus.Failed);
    }

    private void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
