using Velopack;

namespace GitForge.Desktop;

public static class Program
{
    /// <summary>
    /// Velopack runs first: during install, update, and uninstall, Setup and Update.exe start GitForge with hook
    /// arguments, and <see cref="VelopackApp.Run"/> handles them and exits. It also applies an update that was downloaded
    /// but not installed. A normal start (including <c>dotnet run</c>) passes straight through.
    /// </summary>
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
