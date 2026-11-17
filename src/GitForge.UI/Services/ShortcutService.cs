using Microsoft.JSInterop;

namespace GitForge.UI.Services;

/// <summary>Key event forwarded from gitforge.js.</summary>
public sealed record KeyInput(string Key, bool Ctrl, bool Shift, bool Alt, bool Meta, bool InInput);

/// <summary>
/// Keyboard-first command routing. JS captures keys at the document level; this service maps them to
/// command ids that any component can handle. Vim-style keys apply only outside text inputs.
/// </summary>
public sealed class ShortcutService : IAsyncDisposable
{
    public static readonly IReadOnlyList<(string Keys, string Command, string Description)> Map =
    [
        ("Ctrl+P", "palette.open", "Command palette (actions, branches, repositories)"),
        ("Ctrl+Tab / Ctrl+PgDn", "tab.next", "Next repository tab"),
        ("Ctrl+Shift+Tab / Ctrl+PgUp", "tab.prev", "Previous repository tab"),
        ("Ctrl+T", "repo.open", "Open a repository in a new tab"),
        ("Ctrl+W", "tab.close", "Close the current tab"),
        ("Ctrl+Z", "edit.undo", "Undo last action (commit, checkout, branch, reset, discard)"),
        ("Ctrl+Y / Ctrl+Shift+Z", "edit.redo", "Redo"),
        ("Ctrl+1", "view.status", "Select WIP (staging)"),
        ("Ctrl+2", "view.history", "Back to the commit graph"),
        ("Ctrl+3", "view.reflog", "Reflog recovery"),
        ("Ctrl+4", "view.changes", "Changes on the current branch (since it branched)"),
        ("Ctrl+H", "graph.head", "Go to the current branch in the graph"),
        ("Esc", "escape", "Close diff / reflog, return to graph"),
        ("Ctrl+O", "repo.open", "Open repository"),
        ("Ctrl+Shift+O", "repo.clone", "Clone repository"),
        ("F5", "repo.refresh", "Refresh"),
        ("Ctrl+Shift+F", "remote.fetch", "Fetch all remotes"),
        ("Ctrl+Shift+L", "remote.pull", "Pull"),
        ("Ctrl+Shift+P", "remote.push", "Push"),
        ("Ctrl+B", "branch.new", "New branch"),
        ("Ctrl+Shift+S", "stash.save", "Stash changes"),
        ("Ctrl+`", "console.toggle", "Toggle git console"),
        ("Ctrl+F", "search.focus", "Search / filter"),
        ("/", "search.focus", "Search / filter (vim)"),
        ("Ctrl+Enter", "commit.submit", "Commit staged changes"),
        ("j / ↓", "nav.down", "Next item"),
        ("k / ↑", "nav.up", "Previous item"),
        ("g g", "nav.top", "First item"),
        ("Shift+G", "nav.bottom", "Last item"),
        ("h / l", "pane.prev / pane.next", "Move focus: graph ↔ commit panel"),
        ("s", "item.stage", "Stage file / selected lines"),
        ("u", "item.unstage", "Unstage file / selected lines"),
        ("d", "item.discard", "Discard (asks to confirm)"),
        ("Space", "item.toggle", "Toggle line selection in diff"),
        ("m / t / b", "merge.mine / merge.theirs / merge.both", "Conflict editor: take mine / theirs / both"),
        ("n / p", "merge.next / merge.prev", "Conflict editor: next / previous conflict"),
        ("Ctrl+S", "merge.save", "Conflict editor: save and mark resolved"),
        ("?", "help.shortcuts", "Show shortcuts"),
    ];

    private readonly IJSRuntime _js;
    private DotNetObjectReference<ShortcutService>? _ref;
    private string? _pendingG;
    private DateTime _pendingAt;

    public ShortcutService(IJSRuntime js) => _js = js;

    public event Func<string, Task>? Command;

    public bool VimNavigation { get; set; } = true;

    public async Task AttachAsync()
    {
        if (_ref is not null) return;
        _ref = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("gitforge.registerShortcuts", _ref);
    }

    [JSInvokable]
    public async Task<bool> OnKey(KeyInput k)
    {
        var command = Resolve(k);
        if (command is null || Command is null) return false;
        foreach (var handler in Command.GetInvocationList().Cast<Func<string, Task>>())
        {
            await handler(command);
        }

        return true;
    }

    /// <summary>Raises a command programmatically (toolbar buttons share the same handlers as keys).</summary>
    public async Task RaiseAsync(string command)
    {
        if (Command is null) return;
        foreach (var handler in Command.GetInvocationList().Cast<Func<string, Task>>()) await handler(command);
    }

    internal string? Resolve(KeyInput k)
    {
        var key = k.Key;
        if (k.Ctrl || k.Meta)
        {
            return (key.ToLowerInvariant(), k.Shift) switch
            {
                ("1", false) => "view.status",
                ("2", false) => "view.history",
                ("3", false) => "view.reflog",
                ("4", false) => "view.changes",
                ("h", false) => "graph.head",
                ("o", false) => "repo.open",
                ("o", true) => "repo.clone",
                ("f", true) => "remote.fetch",
                ("l", true) => "remote.pull",
                ("p", true) => "remote.push",
                ("b", false) => "branch.new",
                ("s", true) => "stash.save",
                ("`", _) => "console.toggle",
                ("f", false) => "search.focus",
                ("enter", _) => "commit.submit",
                ("p", false) => "palette.open",
                ("tab", false) or ("pagedown", _) => "tab.next",
                ("tab", true) or ("pageup", _) => "tab.prev",
                ("t", false) => "repo.open",
                ("s", false) => "merge.save",
                ("w", false) => "tab.close",
                ("z", false) when !k.InInput => "edit.undo",
                ("z", true) when !k.InInput => "edit.redo",
                ("y", false) when !k.InInput => "edit.redo",
                _ => null,
            };
        }

        if (key == "F5") return "repo.refresh";
        if (key == "Escape") return "escape";
        if (k.InInput || k.Alt) return null;

        switch (key)
        {
            case "ArrowDown": return "nav.down";
            case "ArrowUp": return "nav.up";
            case "Enter": return "nav.open";
        }

        if (!VimNavigation) return null;

        if (key == "g")
        {
            if (_pendingG is not null && (DateTime.UtcNow - _pendingAt).TotalMilliseconds < 600)
            {
                _pendingG = null;
                return "nav.top";
            }

            _pendingG = "g";
            _pendingAt = DateTime.UtcNow;
            return null;
        }

        _pendingG = null;
        return key switch
        {
            "j" => "nav.down",
            "k" => "nav.up",
            "G" => "nav.bottom",
            "h" => "pane.prev",
            "l" => "pane.next",
            "s" => "item.stage",
            "m" => "merge.mine",
            "t" => "merge.theirs",
            "b" => "merge.both",
            "n" => "merge.next",
            "p" => "merge.prev",
            "u" => "item.unstage",
            "d" => "item.discard",
            " " => "item.toggle",
            "/" => "search.focus",
            "?" => "help.shortcuts",
            _ => null,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_ref is null) return;
        try
        {
            await _js.InvokeVoidAsync("gitforge.unregisterShortcuts");
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _ref.Dispose();
    }
}
