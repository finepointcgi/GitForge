using System.IO;
using System.Windows;
using GitForge.UI.Services;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitForge.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        StartupTrace.Mark("window: constructing");
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddGitForge();
        services.AddSingleton<IPlatformShell, WpfPlatformShell>(); // native folder picker overrides the default
        services.AddSingleton<IAppUpdater, VelopackUpdater>();
        var args = Environment.GetCommandLineArgs();
        services.AddSingleton(new StartupOptions(args.Length > 1 ? args[1] : null)); // GitForge.exe <repo-path>

        Resources.Add("services", services.BuildServiceProvider());
        StartupTrace.Mark("window: services built");
        InitializeComponent();
        StartupTrace.Mark("window: XAML loaded");
    }

    private void OnWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        StartupTrace.Mark("webview: creating");
        e.UserDataFolder = WebViewDataFolder();
    }

    /// <summary>
    /// Installed and portable copies keep WebView2 data in their own root folder (beside Velopack's Update.exe), so
    /// uninstalling removes it. A build run from source uses a separate folder: writing into %LOCALAPPDATA%\GitForge before
    /// GitForge was installed made Setup stop to ask whether to overwrite that folder.
    /// </summary>
    private static string WebViewDataFolder()
    {
        var app = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var root = Path.GetDirectoryName(app);
        return string.Equals(Path.GetFileName(app), "current", StringComparison.OrdinalIgnoreCase) && root is not null && File.Exists(Path.Combine(root, "Update.exe"))
            ? Path.Combine(root, "WebView2")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitForge.Dev", "WebView2");
    }

    private void OnWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        StartupTrace.Mark("webview: ready");
        var settings = e.WebView.CoreWebView2.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
#if !DEBUG
        // Desktop app, not a browser: no dev tools, and no F5 reload / Ctrl+P print / browser find.
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
    }
}

/// <summary>Adds the native Windows folder, open, and save pickers.</summary>
internal sealed class WpfPlatformShell : DefaultPlatformShell
{
    public override bool CanPickFolder => true;

    public override bool CanPickFiles => true;

    public override Task<string?> PickFolderAsync(string? initialDirectory = null) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder" };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
        }).Task;

    public override Task<string?> PickOpenFileAsync(string title, string filter, string? initialDirectory = null) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;

    public override Task<string?> PickSaveFileAsync(string title, string fileName, string filter, string? initialDirectory = null) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;
}
