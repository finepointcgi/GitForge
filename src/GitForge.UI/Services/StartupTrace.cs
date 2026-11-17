using System.Diagnostics;
using System.Globalization;

namespace GitForge.UI.Services;

/// <summary>
/// Opt-in boot profiler for support cases ("startup is slow"). Set <c>GITFORGE_TRACE_STARTUP</c> to a file path and
/// milestones from the first two minutes (time since process start, plus step durations) are appended to it.
/// Does nothing when the variable is unset.
/// </summary>
public static class StartupTrace
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("GITFORGE_TRACE_STARTUP") is { Length: > 0 } p ? p : null;
    private static readonly DateTime ProcessStart = LogPath is null ? default : Process.GetCurrentProcess().StartTime;
    private static readonly Lock Sync = new();

    public static bool Enabled => LogPath is not null;

    /// <summary>Records a milestone.</summary>
    public static void Mark(string milestone)
    {
        if (LogPath is null) return;
        var sinceStart = DateTime.Now - ProcessStart;
        if (sinceStart > TimeSpan.FromMinutes(2)) return;
        var line = string.Create(CultureInfo.InvariantCulture, $"{sinceStart.TotalMilliseconds,8:F0} ms  {milestone}{Environment.NewLine}");
        try
        {
            lock (Sync) File.AppendAllText(LogPath, line);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records a milestone with the duration of the step that started at <paramref name="startTimestamp"/>.</summary>
    public static void Step(string step, long startTimestamp)
    {
        if (LogPath is null) return;
        Mark(string.Create(CultureInfo.InvariantCulture, $"{step} ({Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F0} ms)"));
    }
}
