using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GitForge.Core.Abstractions;

namespace GitForge.Git.Cli;

/// <summary>
/// Runs the git executable with an argv list (no shell), UTF-8 I/O, non-interactive prompts,
/// cancellation that kills the whole process tree, and streamed stdout for very large outputs.
/// </summary>
public sealed class GitCli : IGitCli
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly GitEngineOptions _options;
    private string? _gitPath;

    public GitCli(GitEngineOptions options) => _options = options;

    public string GitPath => _gitPath ??= ResolveGitPath(_options.GitExecutablePath);

    public async Task<GitResult> RunAsync(GitCommand command, CancellationToken cancellationToken = default)
    {
        using var process = Start(command);
        using var timeoutCts = command.Timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        await using var registration = linked.Token.Register(() => Kill(process));

        var stdinTask = WriteStdinAsync(process, command.StandardInput);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = ReadStderrAsync(process.StandardError, command.Progress);

        await Task.WhenAll(stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (timeoutCts?.IsCancellationRequested == true) throw new TimeoutException($"{command} timed out after {command.Timeout}.");

        var exit = process.ExitCode;
        return new GitResult(exit, stdoutTask.Result, stderrTask.Result, command.SuccessExitCodes.Contains(exit));
    }

    public async IAsyncEnumerable<string> StreamAsync(GitCommand command, char separator = '\n', [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var process = Start(command);
        await using var registration = cancellationToken.Register(() => Kill(process));
        var stdinTask = WriteStdinAsync(process, command.StandardInput);
        var stderrTask = ReadStderrAsync(process.StandardError, command.Progress);

        var buffer = new char[64 * 1024];
        var record = new StringBuilder(256);
        var reader = process.StandardOutput;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != separator) continue;
                record.Append(buffer, start, i - start);
                yield return record.ToString();
                record.Clear();
                start = i + 1;
            }

            record.Append(buffer, start, read - start);
        }

        if (record.Length > 0) yield return record.ToString();

        await stdinTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!command.SuccessExitCodes.Contains(process.ExitCode))
        {
            throw new GitCommandException(command.ToString(), process.ExitCode, stderr.Trim());
        }
    }

    public async Task<Version?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(new GitCommand(Environment.CurrentDirectory, ["--version"]), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ParseVersion(result.StandardOutput) : null;
    }

    /// <summary>Parses "git version 2.52.0.windows.1" → 2.52.0.</summary>
    internal static Version? ParseVersion(string output)
    {
        var parts = output.Trim().Split(' ');
        if (parts.Length < 3) return null;
        var nums = parts[2].Split('.').Take(3).Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        return nums.Length >= 2 ? new Version(nums[0], nums[1], nums.Length > 2 ? nums[2] : 0) : null;
    }

    private Process Start(GitCommand command)
    {
        if (!Directory.Exists(command.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory not found: {command.WorkingDirectory}");
        }

        var psi = new ProcessStartInfo(GitPath)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        // Stable, parseable output regardless of user config.
        foreach (var arg in (string[])["-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "log.showSignature=false"])
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var arg in command.Arguments) psi.ArgumentList.Add(arg);

        // Never block on a terminal prompt; GUI credential helpers (Git Credential Manager) still work.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Background reads must not take index.lock and race with the user's own git commands.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        if (command.Environment is not null)
        {
            foreach (var (k, v) in command.Environment) psi.Environment[k] = v;
        }

        var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException($"Failed to start git at '{GitPath}'.");
        return process;
    }

    private static async Task WriteStdinAsync(Process process, string? input)
    {
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Process exited before consuming stdin; the exit code reports the real failure.
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    /// <summary>Collects stderr; progress output uses '\r' updates, so split on both '\r' and '\n'.</summary>
    private static async Task<string> ReadStderrAsync(StreamReader reader, IProgress<string>? progress)
    {
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            all.Append(buffer, 0, read);
            if (progress is null) continue;
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0) progress.Report(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (progress is not null && line.Length > 0) progress.Report(line.ToString());
        return all.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static string ResolveGitPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs" })
            {
                var candidate = Path.Combine(root, "Git", "cmd", "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return exe;
    }
}
