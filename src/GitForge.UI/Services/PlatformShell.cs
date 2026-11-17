using System.Diagnostics;

namespace GitForge.UI.Services;

/// <summary>Host start-up options, e.g. a repository path passed on the command line (<c>GitForge.exe C:\src\repo</c>).</summary>
public sealed record StartupOptions(string? OpenPath);

/// <summary>Host-specific shell integration (native folder picker, browser, file explorer).</summary>
public interface IPlatformShell
{
    bool CanPickFolder { get; }
    Task<string?> PickFolderAsync(string? initialDirectory = null);
    void OpenUrl(string url);
    void RevealInFileManager(string path);
}

/// <summary>Default shell: no native picker (paths are typed); opens https URLs and folders with the OS handler.</summary>
public class DefaultPlatformShell : IPlatformShell
{
    public virtual bool CanPickFolder => false;

    public virtual Task<string?> PickFolderAsync(string? initialDirectory = null) => Task.FromResult<string?>(null);

    public void OpenUrl(string url)
    {
        // Only open web links; never hand arbitrary schemes (file:, ms-*, etc.) to the OS.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void RevealInFileManager(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(Directory.Exists(path) ? path : $"/select,{path}");
            Process.Start(psi);
        }
        else
        {
            Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path) { UseShellExecute = false });
        }
    }
}
