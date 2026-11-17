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

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
            Current = settings;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, settings);
    }

    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) =>
        SaveAsync(update(Current), cancellationToken);

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
