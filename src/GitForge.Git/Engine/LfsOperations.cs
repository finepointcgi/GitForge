using System.Globalization;
using System.Text.Json;
using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Git.Engine;

/// <summary>
/// Git LFS via the git-lfs CLI (git-lfs 3.x JSON output). Locking talks to the LFS server using the user's
/// normal git credentials; failures are reported in <see cref="LfsLockList.Error"/> rather than thrown,
/// because many servers do not support the locking API.
/// </summary>
internal sealed class LfsOperations(RepoContext ctx) : ILfsOperations
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        (await ctx.RunAsync(["lfs", "version"], cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<IReadOnlyList<LfsPattern>> GetPatternsAsync(CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["lfs", "track"], cancellationToken).ConfigureAwait(false);
        return r.Succeeded ? ParsePatterns(r.StandardOutput) : [];
    }

    public async Task<IReadOnlyList<LfsFile>> GetFilesAsync(CancellationToken cancellationToken = default)
    {
        var r = await ctx.RunAsync(["lfs", "ls-files", "--json"], cancellationToken).ConfigureAwait(false);
        return r.Succeeded ? ParseFiles(r.StandardOutput) : [];
    }

    public async Task<LfsLockList> GetLocksAsync(CancellationToken cancellationToken = default)
    {
        var verified = await ctx.RunAsync(["lfs", "locks", "--verify", "--json"], cancellationToken).ConfigureAwait(false);
        if (verified.Succeeded) return ParseVerifiedLocks(verified.StandardOutput);

        var plain = await ctx.RunAsync(["lfs", "locks", "--json"], cancellationToken).ConfigureAwait(false);
        return plain.Succeeded
            ? new LfsLockList(ParseLockArray(plain.StandardOutput, null), false, null)
            : new LfsLockList([], false, FirstLine(plain.StandardError) ?? "The LFS server did not return locks.");
    }

    public async Task<LfsLock> LockAsync(string path, CancellationToken cancellationToken = default)
    {
        var r = await ctx.WriteAsync("lfs lock", ["lfs", "lock", "--json", Guard(path)], cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(r.StandardOutput);
        return ParseLock(doc.RootElement, isOurs: true) ?? throw new InvalidOperationException("The LFS server returned an unexpected response.");
    }

    public Task UnlockAsync(string path, bool force, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("lfs unlock", force ? ["lfs", "unlock", "--force", "--json", Guard(path)] : ["lfs", "unlock", "--json", Guard(path)], cancellationToken);

    public Task TrackAsync(string pattern, bool lockable, CancellationToken cancellationToken = default) =>
        ctx.WriteAsync("lfs track", lockable ? ["lfs", "track", "--lockable", Guard(pattern)] : ["lfs", "track", Guard(pattern)], cancellationToken);

    /// <summary>git-lfs takes paths and patterns positionally without "--"; refuse anything that looks like an option.</summary>
    private static string Guard(string value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith('-') ? throw new ArgumentException($"Invalid LFS path or pattern '{value}'.", nameof(value)) : value;

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>"    *.psd [lockable] (.gitattributes)" lines between "Listing tracked patterns" and "Listing excluded patterns".</summary>
    internal static IReadOnlyList<LfsPattern> ParsePatterns(string output)
    {
        var list = new List<LfsPattern>();
        var inTracked = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Listing tracked patterns", StringComparison.Ordinal)) { inTracked = true; continue; }
            if (line.StartsWith("Listing excluded patterns", StringComparison.Ordinal)) { inTracked = false; continue; }
            if (!inTracked || line.Trim().Length == 0) continue;

            var text = line.Trim();
            var source = string.Empty;
            var paren = text.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && text.EndsWith(')'))
            {
                source = text[(paren + 2)..^1];
                text = text[..paren];
            }

            var lockable = text.EndsWith(" [lockable]", StringComparison.Ordinal);
            if (lockable) text = text[..^" [lockable]".Length];
            list.Add(new LfsPattern(text, lockable, source));
        }

        return list;
    }

    internal static IReadOnlyList<LfsFile> ParseFiles(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];
        return files.EnumerateArray().Select(f => new LfsFile(
            Str(f, "name") ?? string.Empty,
            f.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
            Str(f, "oid") ?? string.Empty,
            f.TryGetProperty("downloaded", out var d) ? d.ValueKind == JsonValueKind.True : true)).ToList();
    }

    /// <summary><c>{"ours":[...],"theirs":[...]}</c></summary>
    internal static LfsLockList ParseVerifiedLocks(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var locks = new List<LfsLock>();
        foreach (var (prop, ours) in new[] { ("ours", true), ("theirs", false) })
        {
            if (!doc.RootElement.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            locks.AddRange(arr.EnumerateArray().Select(e => ParseLock(e, ours)).OfType<LfsLock>());
        }

        return new LfsLockList(locks.OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList(), true, null);
    }

    internal static IReadOnlyList<LfsLock> ParseLockArray(string json, bool? isOurs)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(e => ParseLock(e, isOurs)).OfType<LfsLock>().ToList()
            : [];
    }

    private static LfsLock? ParseLock(JsonElement e, bool? isOurs)
    {
        var path = Str(e, "path");
        if (path is null) return null;
        var owner = e.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, "name") : null;
        DateTimeOffset? at = DateTimeOffset.TryParse(Str(e, "locked_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
        return new LfsLock(Str(e, "id") ?? string.Empty, path, owner ?? "unknown", at, isOurs);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;
}
