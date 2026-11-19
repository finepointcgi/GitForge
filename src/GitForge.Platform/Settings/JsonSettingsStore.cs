using System.Text.Json;
using System.Text.Json.Serialization;
using GitForge.Core.Abstractions;
using GitForge.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace GitForge.Platform.Settings;

/// <summary>Per-user JSON settings with atomic writes. Contains no secrets (tokens live in the OS keyring).</summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitForge", "settings.json");
    }

    public string FilePath => _path;
    public AppSettings Current { get; private set; } = new();
    public event EventHandler<AppSettings>? Changed;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
            {
                try
                {
                    await using var stream = File.OpenRead(_path);
                    Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
                    if (string.Equals(Current.AccentColor, AppSettings.LegacyDefaultAccentColor, StringComparison.OrdinalIgnoreCase))
                    {
                        Current = Current with { AccentColor = AppSettings.DefaultAccentColor }; // never customized: follow the new default
                    }
                }
                catch (JsonException)
                {
                    // Corrupt file: keep a copy for diagnosis and start from defaults.
                    File.Copy(_path, _path + ".corrupt", overwrite: true);
                    Current = new AppSettings();
                }
            }

            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        UpdateAsync(_ => settings, cancellationToken);

    /// <summary>
    /// Applies <paramref name="update"/> to the current settings and writes the result. The read, change, and write happen
    /// under one lock, so concurrent updates (tab changes, column widths, update checks) never overwrite each other.
    /// </summary>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings = update(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            await ReplaceAsync(temp, cancellationToken).ConfigureAwait(false);
            Current = settings;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, settings);
    }

    /// <summary>
    /// Moves the new file over settings.json. Windows refuses while another process briefly has the old file open (antivirus
    /// or the search indexer scanning the previous write), so retry for up to about half a second before giving up.
    /// </summary>
    private async Task ReplaceAsync(string temp, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, _path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                await Task.Delay(15 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddGitForgePlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISecretStore>(_ => SecretStoreFactory.CreateDefault());
        // GITFORGE_SETTINGS selects an alternate settings file (portable installs, isolated test runs).
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(Environment.GetEnvironmentVariable("GITFORGE_SETTINGS") is { Length: > 0 } p ? p : null));
        return services;
    }
}
