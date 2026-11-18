using System.IO;
using System.Reflection;
using GitForge.UI.Services;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace GitForge.Desktop;

/// <summary>
/// Updates an installed GitForge with Velopack. Velopack checks each downloaded package's SHA-256 against the feed's
/// release index before installing it. The feed is GitHub releases unless policy or settings name another one.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    private const string PolicyKey = @"SOFTWARE\Policies\GitForge";

    private readonly bool _installed;
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public VelopackUpdater()
    {
        DefaultFeed = typeof(VelopackUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitForgeUpdateFeed")?.Value ?? string.Empty;
        Policy = ReadPolicy();

        // IsInstalled and CurrentVersion come from the install folder, not the source, so any source works for this probe.
        var probe = new UpdateManager(new SimpleFileSource(new DirectoryInfo(AppContext.BaseDirectory)));
        _installed = probe.IsInstalled;
        CurrentVersion = probe.CurrentVersion?.ToString() ?? AppVersion.Of(typeof(VelopackUpdater));
    }

    public string CurrentVersion { get; }

    public string? UnavailableReason => _installed
        ? null
        : "This copy runs from a build folder, so it can't update itself. Install GitForge from its releases to get updates.";

    public UpdatePolicy Policy { get; }

    public string DefaultFeed { get; }

    public async Task<AppUpdate?> CheckAsync(UpdateFeed feed, CancellationToken cancellationToken = default)
    {
        IUpdateSource source = feed.Kind switch
        {
            UpdateFeedKind.GitHub => new GithubSource(feed.Location, accessToken: null, prerelease: false),
            UpdateFeedKind.Web => new SimpleWebSource(feed.Location),
            _ => new SimpleFileSource(new DirectoryInfo(feed.Location)),
        };
        var manager = new UpdateManager(source);
        var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        (_manager, _pending) = info is null ? (null, null) : (manager, info);
        return info is null ? null : new AppUpdate(info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown);
    }

    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken = default)
    {
        var (manager, info) = Pending(update);
        return manager.DownloadUpdatesAsync(info, progress, cancellationToken);
    }

    public void ApplyAndRestart(AppUpdate update)
    {
        var (manager, info) = Pending(update);
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    private (UpdateManager Manager, UpdateInfo Info) Pending(AppUpdate update) =>
        _manager is { } m && _pending is { } p && p.TargetFullRelease.Version.ToString() == update.Version
            ? (m, p)
            : throw new InvalidOperationException("Check for updates again; this update is no longer pending.");

    /// <summary>DisableUpdates (DWORD 1) and UpdateFeed (string). Either hive can turn updates off; the machine's feed wins.</summary>
    private static UpdatePolicy ReadPolicy()
    {
        var disabled = false;
        string? feed = null;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(PolicyKey);
                if (key is null) continue;
                disabled |= key.GetValue("DisableUpdates") is int d && d != 0;
                feed ??= key.GetValue("UpdateFeed") is string f && !string.IsNullOrWhiteSpace(f) ? f.Trim() : null;
            }
            catch (System.Security.SecurityException)
            {
                // Unreadable policy key: treat as not set.
            }
        }

        return new UpdatePolicy(disabled, feed);
    }
}
