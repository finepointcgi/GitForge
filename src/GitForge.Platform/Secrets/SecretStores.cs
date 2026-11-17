using System.Diagnostics;
using System.Runtime.Versioning;
using GitForge.Core.Abstractions;

namespace GitForge.Platform.Secrets;

/// <summary>Linux Secret Service (GNOME Keyring / KWallet) via <c>secret-tool</c>. Secrets travel over stdin/stdout only, never argv.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretToolStore : ISecretStore
{
    private const string Service = "gitforge";

    public string BackendName => "Secret Service (libsecret)";
    public bool IsSupported => File.Exists("/usr/bin/secret-tool") || File.Exists("/bin/secret-tool");

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var (exit, stdout) = await RunAsync(["lookup", "service", Service, "key", key], null, cancellationToken).ConfigureAwait(false);
        return exit == 0 && stdout.Length > 0 ? stdout : null;
    }

    public async Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        var (exit, _) = await RunAsync(["store", $"--label=GitForge {key}", "service", Service, "key", key], secret, cancellationToken).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException($"secret-tool store failed (exit {exit}).");
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        await RunAsync(["clear", "service", Service, "key", key], null, cancellationToken).ConfigureAwait(false);

    private static async Task<(int Exit, string Stdout)> RunAsync(string[] args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start secret-tool.");
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, output);
    }
}

/// <summary>Placeholder for platforms without a native backend yet. Refuses to store secrets rather than falling back to plaintext.</summary>
public sealed class UnsupportedSecretStore(string platform) : ISecretStore
{
    public string BackendName => $"Not available on {platform}";
    public bool IsSupported => false;
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException($"No secure keyring backend is available on {platform}. Tokens are never stored in plaintext.");

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static class SecretStoreFactory
{
    public static ISecretStore CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        if (OperatingSystem.IsLinux()) return new LinuxSecretToolStore();
        return new UnsupportedSecretStore(OperatingSystem.IsMacOS() ? "macOS (Keychain backend planned)" : "this OS");
    }
}
