using System.Diagnostics;

namespace GitForge.UI.Services;

/// <summary>Host start-up options, e.g. a repository path passed on the command line (<c>GitForge.exe C:\src\repo</c>).</summary>
public sealed record StartupOptions(string? OpenPath);

/// <summary>Host-specific shell integration (native pickers, browser, file explorer, terminal, editor).</summary>
public interface IPlatformShell
{
    bool CanPickFolder { get; }
    Task<string?> PickFolderAsync(string? initialDirectory = null);

    /// <summary>Native open and save dialogs are available (otherwise paths are typed).</summary>
    bool CanPickFiles { get; }

    /// <param name="filter">"Description|*.ext;*.ext2" pairs separated by "|", as in Windows file dialogs.</param>
    Task<string?> PickOpenFileAsync(string title, string filter, string? initialDirectory = null);

    Task<string?> PickSaveFileAsync(string title, string fileName, string filter, string? initialDirectory = null);

    void OpenUrl(string url);
    void RevealInFileManager(string path);

    /// <summary>Opens a file with its default app. Programs and scripts are refused (returns false) so a repository can't run code.</summary>
    bool OpenFile(string path);

    /// <summary>Opens a terminal in <paramref name="directory"/>; see <see cref="ExternalTools.TerminalCandidates"/>. False when none was found.</summary>
    bool OpenTerminal(string directory, string? command, string? gitPath);

    /// <summary>Opens a file or folder in the editor; false when no editor is configured or found.</summary>
    bool OpenInEditor(string path, string? command);
}

/// <summary>Default shell: no native pickers (paths are typed); opens URLs, folders, files, terminals, and editors with the OS.</summary>
public class DefaultPlatformShell : IPlatformShell
{
    public virtual bool CanPickFolder => false;

    public virtual Task<string?> PickFolderAsync(string? initialDirectory = null) => Task.FromResult<string?>(null);

    public virtual bool CanPickFiles => false;

    public virtual Task<string?> PickOpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

    public virtual Task<string?> PickSaveFileAsync(string title, string fileName, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

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

    public bool OpenFile(string path)
    {
        if (!File.Exists(path) || ExternalTools.IsRunnable(path)) return false;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }

    public bool OpenTerminal(string directory, string? command, string? gitPath)
    {
        if (!Directory.Exists(directory)) return false;
        if (!string.IsNullOrWhiteSpace(command)) return Start(ExternalTools.Build(command, directory), directory);

        foreach (var candidate in ExternalTools.TerminalCandidates(directory, gitPath))
        {
            if (Start(candidate, directory)) return true;
        }

        return false;
    }

    public bool OpenInEditor(string path, string? command)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var launch = string.IsNullOrWhiteSpace(command)
            ? ExternalTools.FindVsCode() is { } code ? new ExternalCommand(code, [path]) : null
            : ExternalTools.Build(command, path);
        return Start(launch, Directory.Exists(path) ? path : Path.GetDirectoryName(path));
    }

    private static bool Start(ExternalCommand? command, string? workingDirectory)
    {
        if (command is null) return false;
        var psi = new ProcessStartInfo(command.FileName) { UseShellExecute = command.UseShell, WorkingDirectory = workingDirectory ?? string.Empty };
        if (command.UseShell) psi.Arguments = string.Join(' ', command.Arguments); // fixed flags only; paths never go through the shell
        else foreach (var a in command.Arguments) psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // not installed / not on PATH
        }
    }
}

/// <summary>A program to start with an argument list (never through a shell, so paths can't inject commands).</summary>
public sealed record ExternalCommand(string FileName, IReadOnlyList<string> Arguments, bool UseShell = false);

/// <summary>Finds and builds the commands for terminals and editors, and decides which files are safe to open.</summary>
public static class ExternalTools
{
    public const string PathPlaceholder = "{path}";

    /// <summary>Extensions Windows runs rather than opens; GitForge never launches these from a repository.</summary>
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".mst", ".scr", ".pif", ".lnk", ".url", ".hta", ".cpl", ".reg", ".jar", ".application", ".appref-ms", ".gadget", ".msc",
        ".inf", ".scf", ".sct", ".chm", ".dll", ".sys", ".ocx", ".library-ms", ".settingcontent-ms", ".appx", ".msix", ".appinstaller",
    };

    public static bool IsRunnable(string path) => Runnable.Contains(Path.GetExtension(path));

    /// <summary>
    /// Splits a command template ("wt.exe -d {path}", "\"C:\Program Files\X\x.exe\" --new {path}") into a program and
    /// arguments, replacing {path} (or appending the path when there is no placeholder). Null for empty templates and for
    /// .cmd/.bat programs, which Windows runs through cmd.exe, where a file name could inject commands.
    /// </summary>
    public static ExternalCommand? Build(string template, string path)
    {
        var argv = CommandLineSplitter.Split(template.Trim());
        if (argv.Count == 0 || Path.GetExtension(argv[0]) is var ext && (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var args = argv.Skip(1).ToList();
        if (args.Any(a => a.Contains(PathPlaceholder, StringComparison.Ordinal)))
        {
            args = args.Select(a => a.Replace(PathPlaceholder, path, StringComparison.Ordinal)).ToList();
        }
        else
        {
            args.Add(path);
        }

        return new ExternalCommand(argv[0], args);
    }

    /// <summary>Default terminals in order: Windows Terminal, Git Bash, then PowerShell in a new window.</summary>
    public static IEnumerable<ExternalCommand> TerminalCandidates(string directory, string? gitPath)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt)) yield return new ExternalCommand(wt, ["-d", directory.Replace(";", "\\;", StringComparison.Ordinal)]); // ";" separates wt commands
        if (FindGitBash(gitPath) is { } bash) yield return new ExternalCommand(bash, [$"--cd={directory}"]);
        yield return new ExternalCommand("powershell.exe", ["-NoExit"], UseShell: true);
    }

    /// <summary>git-bash.exe beside the git in use (…\Git\cmd\git.exe → …\Git\git-bash.exe), or in the default install folders.</summary>
    public static string? FindGitBash(string? gitPath)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(gitPath) && Path.IsPathRooted(gitPath))
        {
            for (var dir = Path.GetDirectoryName(gitPath); dir is not null; dir = Path.GetDirectoryName(dir)) roots.Add(dir);
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"));
        return roots.Select(r => Path.Combine(r, "git-bash.exe")).FirstOrDefault(File.Exists);
    }

    /// <summary>Visual Studio Code's executable (user or system install). Code.exe is started directly; code.cmd would need cmd.exe.</summary>
    public static string? FindVsCode() =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        }.FirstOrDefault(File.Exists);
}
