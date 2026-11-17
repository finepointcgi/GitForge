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
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddGitForge();
        services.AddSingleton<IPlatformShell, WpfPlatformShell>(); // native folder picker overrides the default
        var args = Environment.GetCommandLineArgs();
        services.AddSingleton(new StartupOptions(args.Length > 1 ? args[1] : null)); // GitForge.exe <repo-path>

        Resources.Add("services", services.BuildServiceProvider());
        InitializeComponent();
    }

    private void OnWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        // Keep WebView2 state in the user profile (install folders may be read-only).
        e.UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitForge", "WebView2");
    }

    private void OnWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
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

/// <summary>Adds the native Windows folder picker.</summary>
internal sealed class WpfPlatformShell : DefaultPlatformShell
{
    public override bool CanPickFolder => true;

    public override Task<string?> PickFolderAsync(string? initialDirectory = null) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder" };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
        }).Task;
}
