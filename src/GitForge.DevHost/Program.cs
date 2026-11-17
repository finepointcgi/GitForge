using System.Net;
using GitForge.DevHost.Components;
using GitForge.UI.Services;

// Development-only host. It grants full git control of this machine to the browser session,
// so it binds to loopback and rejects cross-origin and non-loopback Host headers (DNS rebinding).
var builder = WebApplication.CreateBuilder(args);
var url = builder.Configuration["GitForge:Url"] ?? "http://127.0.0.1:5199";
if (!Uri.TryCreate(url, UriKind.Absolute, out var bind) || !IPAddress.TryParse(bind.Host, out var ip) || !IPAddress.IsLoopback(ip))
{
    throw new InvalidOperationException("GitForge.DevHost may only bind to a loopback IP address (127.0.0.1 or [::1]).");
}

builder.WebHost.UseUrls(url);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddGitForge();
if (builder.Configuration["GitForge:SettingsPath"] is { Length: > 0 } settingsPath)
{
    // Isolated settings for automated UI checks; never touches the developer's real profile.
    builder.Services.AddSingleton<GitForge.Core.Abstractions.ISettingsStore>(_ => new GitForge.Platform.Settings.JsonSettingsStore(settingsPath));
}

var app = builder.Build();

app.Use(async (context, next) =>
{
    static bool IsLoopbackHost(string host) =>
        host is "localhost" || (IPAddress.TryParse(host.Trim('[', ']'), out var a) && IPAddress.IsLoopback(a));

    var origin = context.Request.Headers.Origin.ToString();
    var originOk = origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var o) && IsLoopbackHost(o.Host) && o.Port == context.Request.Host.Port);
    if (!IsLoopbackHost(context.Request.Host.Host) || !originOk)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
