using System.Runtime.CompilerServices;
using GitForge.Core.Abstractions;

namespace GitForge.UI.Services;

/// <summary>A command that keys can be bound to. <see cref="Defaults"/> use display syntax ("Ctrl+Shift+F", "g g").</summary>
public sealed record ShortcutCommand(string Id, string Group, string Description, IReadOnlyList<string> Defaults);

/// <summary>
/// The shortcut catalog and key-chord handling. Chords are kept in a canonical, keyboard-layout-aware form: modifiers in
/// the order ctrl, alt, shift, then the key as the browser reports it, lower-cased ("ctrl+shift+f", "shift+g", "j",
/// "space", "arrowdown", "f5", "?"). Shift is part of a chord only for letters and named keys; for other characters the
/// character already says it ("?" is Shift+/ on a US keyboard). A two-step sequence separates its chords with a space.
/// gitforge.js normalizes key events the same way, so the two sides always agree on what a chord is.
/// </summary>
public static class Keymap
{
    public static readonly IReadOnlyList<ShortcutCommand> Commands =
    [
        new("palette.open", "General", "Command palette (actions, branches, repositories)", ["Ctrl+P"]),
        new("help.shortcuts", "General", "Keyboard shortcuts", ["?"]),
        new("repo.open", "General", "Open a repository in a new tab", ["Ctrl+O", "Ctrl+T"]),
        new("repo.clone", "General", "Clone a repository", ["Ctrl+Shift+O"]),
        new("tab.next", "General", "Next repository tab", ["Ctrl+Tab", "Ctrl+PgDn"]),
        new("tab.prev", "General", "Previous repository tab", ["Ctrl+Shift+Tab", "Ctrl+PgUp"]),
        new("tab.close", "General", "Close the current tab", ["Ctrl+W"]),
        new("repo.refresh", "General", "Refresh", ["F5"]),
        new("edit.undo", "General", "Undo the last action (commit, checkout, branch, reset, discard)", ["Ctrl+Z"]),
        new("edit.redo", "General", "Redo", ["Ctrl+Y", "Ctrl+Shift+Z"]),
        new("settings.open", "General", "Settings", []),
        new("accounts.open", "General", "Hosting accounts", []),
        new("theme.toggle", "General", "Toggle light and dark theme", []),

        new("view.status", "Views", "Select WIP (stage and commit)", ["Ctrl+1"]),
        new("view.history", "Views", "Commit graph", ["Ctrl+2"]),
        new("view.reflog", "Views", "Reflog (recover lost work)", ["Ctrl+3"]),
        new("view.changes", "Views", "Changes on the current branch since it branched", ["Ctrl+4"]),
        new("graph.head", "Views", "Go to the current branch in the graph", ["Ctrl+H"]),
        new("search.focus", "Views", "Filter commits", ["Ctrl+F", "/"]),
        new("escape", "Views", "Back to the graph; clear the filter or selection", ["Esc"]),
        new("console.toggle", "Views", "Git console", ["Ctrl+`"]),
        new("prs.open", "Views", "Pull requests", []),

        new("remote.fetch", "Remotes", "Fetch all remotes", ["Ctrl+Shift+F"]),
        new("remote.pull", "Remotes", "Pull", ["Ctrl+Shift+L"]),
        new("remote.push", "Remotes", "Push", ["Ctrl+Shift+P"]),

        new("branch.new", "Branches and commits", "New branch", ["Ctrl+B"]),
        new("commit.submit", "Branches and commits", "Commit staged changes", ["Ctrl+Enter"]),
        new("stash.save", "Branches and commits", "Stash changes", ["Ctrl+Shift+S"]),
        new("stash.pop", "Branches and commits", "Pop the latest stash", []),

        new("nav.down", "Navigation", "Next item", ["j", "↓"]),
        new("nav.up", "Navigation", "Previous item", ["k", "↑"]),
        new("nav.top", "Navigation", "First item", ["g g"]),
        new("nav.bottom", "Navigation", "Last item", ["Shift+G"]),
        new("nav.open", "Navigation", "Open the item (graph → commit panel → diff)", ["Enter"]),
        new("select.down", "Navigation", "Extend the commit selection down", ["Shift+↓", "Shift+J"]),
        new("select.up", "Navigation", "Extend the commit selection up", ["Shift+↑", "Shift+K"]),
        new("pane.prev", "Navigation", "Focus the graph", ["h"]),
        new("pane.next", "Navigation", "Focus the commit panel", ["l"]),

        new("item.stage", "Staging", "Stage the file, hunk, or selected lines", ["s"]),
        new("item.unstage", "Staging", "Unstage the file, hunk, or selected lines", ["u"]),
        new("item.discard", "Staging", "Discard (asks first)", ["d"]),
        new("item.toggle", "Staging", "Select or deselect a diff line", ["Space"]),

        new("merge.mine", "Conflict editor", "Take mine", ["m"]),
        new("merge.theirs", "Conflict editor", "Take theirs", ["t"]),
        new("merge.both", "Conflict editor", "Take both", ["b"]),
        new("merge.next", "Conflict editor", "Next conflict", ["n"]),
        new("merge.prev", "Conflict editor", "Previous conflict", ["p"]),
        new("merge.save", "Conflict editor", "Save and mark resolved", ["Ctrl+S"]),
    ];

    private static readonly Dictionary<string, ShortcutCommand> ById = Commands.ToDictionary(c => c.Id, StringComparer.Ordinal);

    private static readonly HashSet<string> NamedKeys = new(StringComparer.Ordinal)
    {
        "enter", "escape", "tab", "space", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
        "arrowup", "arrowdown", "arrowleft", "arrowright", "contextmenu",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "escape", ["return"] = "enter", ["del"] = "delete", ["ins"] = "insert", ["spacebar"] = "space",
        ["pgdn"] = "pagedown", ["pgup"] = "pageup", ["up"] = "arrowup", ["down"] = "arrowdown", ["left"] = "arrowleft", ["right"] = "arrowright",
        ["↑"] = "arrowup", ["↓"] = "arrowdown", ["←"] = "arrowleft", ["→"] = "arrowright",
    };

    private static readonly Dictionary<string, string> KeyLabels = new(StringComparer.Ordinal)
    {
        ["escape"] = "Esc", ["enter"] = "Enter", ["tab"] = "Tab", ["space"] = "Space", ["backspace"] = "Backspace",
        ["delete"] = "Del", ["insert"] = "Ins", ["home"] = "Home", ["end"] = "End", ["pageup"] = "PgUp", ["pagedown"] = "PgDn",
        ["arrowup"] = "↑", ["arrowdown"] = "↓", ["arrowleft"] = "←", ["arrowright"] = "→", ["contextmenu"] = "Menu",
    };

    /// <summary>Keys that must keep their usual meaning, with the reason shown to the user.</summary>
    private static readonly Dictionary<string, string> ReservedChords = new(StringComparer.Ordinal)
    {
        ["tab"] = "moves keyboard focus",
        ["shift+tab"] = "moves keyboard focus",
        ["ctrl+c"] = "copies text",
        ["ctrl+x"] = "cuts text",
        ["ctrl+v"] = "pastes text",
        ["ctrl+a"] = "selects all text",
        ["alt+f4"] = "closes the window",
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoOverrides = new Dictionary<string, IReadOnlyList<string>>();
    private static readonly ConditionalWeakTable<AppSettings, IReadOnlyDictionary<string, IReadOnlyList<string>>> Cache = new();

    public static ShortcutCommand? Find(string commandId) => ById.GetValueOrDefault(commandId);

    /// <summary>User overrides from <paramref name="settings"/> (never null, even for a hand-edited settings file).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Overrides(AppSettings settings) => settings.KeyBindings ?? NoOverrides;

    /// <summary>Every command's chords under <paramref name="settings"/>: the user's list when changed, else the defaults.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Resolve(AppSettings settings) => Cache.GetValue(settings, Build);

    public static IReadOnlyList<string> Effective(AppSettings settings, string commandId) =>
        Resolve(settings).TryGetValue(commandId, out var chords) ? chords : [];

    /// <summary>The first chord bound to a command, for display ("Ctrl+H"), or null when it has none.</summary>
    public static string? Label(AppSettings settings, string commandId) =>
        Effective(settings, commandId) is [var first, ..] ? Display(first) : null;

    /// <summary>" (Ctrl+H)" for tooltips and menus, or empty when the command has no shortcut.</summary>
    public static string Hint(AppSettings settings, string commandId) =>
        Label(settings, commandId) is { } label ? $" ({label})" : string.Empty;

    /// <summary>How to undo, for messages: the undo shortcut, or "Undo" (the toolbar button) when it has none.</summary>
    public static string UndoName(AppSettings settings) => Label(settings, "edit.undo") ?? "Undo";

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(AppSettings settings)
    {
        var overrides = Overrides(settings);
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var c in Commands)
        {
            var source = overrides.TryGetValue(c.Id, out var custom) && custom is not null ? custom : c.Defaults;
            map[c.Id] = source.Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        }

        return map;
    }

    // ---------- Chords ----------

    /// <summary>Canonical form of a chord written by a person or stored in settings, or null when it is not a valid chord.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var steps = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (steps.Length > 2) return null;
        var parsed = steps.Select(ParseStep).ToList();
        return parsed.Any(p => p is null) ? null : string.Join(' ', parsed);
    }

    private static string? ParseStep(string step)
    {
        string key;
        string[] modifiers;
        if (step.Length > 1 && step.EndsWith("++", StringComparison.Ordinal))
        {
            key = "+";
            modifiers = step[..^2].Split('+');
        }
        else
        {
            var parts = step.Split('+');
            key = parts[^1];
            modifiers = parts[..^1];
        }

        bool ctrl = false, alt = false, shift = false;
        foreach (var m in modifiers)
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd" or "command" or "meta": ctrl = true; break;
                case "alt" or "option": alt = true; break;
                case "shift": shift = true; break;
                default: return null;
            }
        }

        var name = KeyName(key);
        return name is null ? null : Compose(ctrl, alt, shift, name);
    }

    /// <summary>Canonical chord for a key event, or null for keys that never form a chord on their own (Shift, Ctrl…).</summary>
    public static string? FromInput(KeyInput k)
    {
        if (string.IsNullOrEmpty(k.Key) || (k.Key.Length > 1 && !IsNamedKey(k.Key.ToLowerInvariant()))) return null;
        var name = KeyName(k.Key);
        return name is null ? null : Compose(k.Ctrl || k.Meta, k.Alt, k.Shift, name);
    }

    private static string Compose(bool ctrl, bool alt, bool shift, string name)
    {
        // Shift only counts where it doesn't already pick the character: letters and named keys (Space is a character).
        var shiftCounts = shift && (name.Length == 1 ? IsLetter(name[0]) : name != "space");
        return (ctrl ? "ctrl+" : string.Empty) + (alt ? "alt+" : string.Empty) + (shiftCounts ? "shift+" : string.Empty) + name;
    }

    /// <summary>A cased letter, tested the way gitforge.js does (lower and upper case differ).</summary>
    private static bool IsLetter(char c) => char.ToLowerInvariant(c) != char.ToUpperInvariant(c);

    private static string? KeyName(string key)
    {
        if (Aliases.TryGetValue(key, out var alias)) return alias;
        if (key.Length == 1) return key == " " ? "space" : char.IsControl(key[0]) ? null : char.ToLowerInvariant(key[0]).ToString();
        var lower = key.ToLowerInvariant();
        return IsNamedKey(lower) ? lower : null;
    }

    private static bool IsNamedKey(string lower) =>
        NamedKeys.Contains(lower) || (lower.Length is 2 or 3 && lower[0] == 'f' && int.TryParse(lower.AsSpan(1), out var n) && n is >= 1 and <= 24);

    /// <summary>Human-readable chord: "Ctrl+Shift+F", "Shift+G", "j", "g g", "Space", "↓".</summary>
    public static string Display(string chord) => string.Join(' ', chord.Split(' ').Select(DisplayStep));

    private static string DisplayStep(string step)
    {
        var (mods, key) = Split(step);
        var label = KeyLabels.TryGetValue(key, out var named) ? named
            : key.Length > 1 ? char.ToUpperInvariant(key[0]) + key[1..] // f5 → F5
            : mods.Count > 0 ? key.ToUpperInvariant() : key;            // Ctrl+P, but plain j
        return string.Concat(mods.Select(m => m switch { "ctrl" => "Ctrl+", "alt" => "Alt+", _ => "Shift+" })) + label;
    }

    private static (List<string> Modifiers, string Key) Split(string step)
    {
        var mods = new List<string>();
        var rest = step;
        foreach (var m in new[] { "ctrl+", "alt+", "shift+" })
        {
            if (rest.Length > m.Length && rest.StartsWith(m, StringComparison.Ordinal))
            {
                mods.Add(m[..^1]);
                rest = rest[m.Length..];
            }
        }

        return (mods, rest);
    }

    /// <summary>
    /// Single-key shortcuts (a character with at most Shift, like j, ?, Shift+G, Space) only work outside text fields and
    /// only while vim-style navigation is on, so they never get in the way of typing.
    /// </summary>
    public static bool IsSingleKey(string chord)
    {
        var (mods, key) = Split(chord.Split(' ')[0]);
        return !mods.Contains("ctrl") && !mods.Contains("alt") && (key.Length == 1 || key == "space");
    }

    /// <summary>Chords with Ctrl or Alt, function keys, and Esc also fire while typing in a text field (except undo/redo, which stay native there).</summary>
    public static bool WorksInTextFields(string chord, string commandId)
    {
        if (chord.Contains(' ', StringComparison.Ordinal) || commandId is "edit.undo" or "edit.redo") return false;
        var (mods, key) = Split(chord);
        return mods.Contains("ctrl") || mods.Contains("alt") || key == "escape" || (key.Length > 1 && key[0] == 'f' && char.IsDigit(key[1]));
    }

    /// <summary>Why a chord can't be assigned, or null when it can.</summary>
    public static string? ReservedReason(string chord) => ReservedChords.GetValueOrDefault(chord);

    // ---------- Editing ----------

    /// <summary>
    /// The command that already uses <paramref name="chord"/>, or null. A chord also clashes with a sequence that starts
    /// with it ("g" and "g g"), because one would hide the other.
    /// </summary>
    public static string? OwnerOf(AppSettings settings, string chord)
    {
        foreach (var (id, chords) in Resolve(settings))
        {
            if (chords.Any(c => Clashes(c, chord))) return id;
        }

        return null;
    }

    private static bool Clashes(string a, string b) =>
        a == b || a.StartsWith(b + " ", StringComparison.Ordinal) || b.StartsWith(a + " ", StringComparison.Ordinal);

    /// <summary>Adds <paramref name="chord"/> to a command, taking it (and any clashing sequence) away from other commands.</summary>
    public static AppSettings WithBinding(AppSettings settings, string commandId, string chord)
    {
        if (Effective(settings, commandId).Contains(chord)) return settings;
        var overrides = new Dictionary<string, IReadOnlyList<string>>(Overrides(settings), StringComparer.Ordinal);
        foreach (var (id, chords) in Resolve(settings))
        {
            var kept = chords.Where(c => !Clashes(c, chord)).ToList();
            if (id == commandId) kept.Add(chord);
            if (!kept.SequenceEqual(chords, StringComparer.Ordinal)) SetOverride(overrides, id, kept);
        }

        return settings with { KeyBindings = overrides };
    }

    public static AppSettings WithoutBinding(AppSettings settings, string commandId, string chord)
    {
        var overrides = new Dictionary<string, IReadOnlyList<string>>(Overrides(settings), StringComparer.Ordinal);
        SetOverride(overrides, commandId, Effective(settings, commandId).Where(c => c != chord).ToList());
        return settings with { KeyBindings = overrides };
    }

    public static AppSettings Reset(AppSettings settings, string? commandId)
    {
        if (commandId is null) return settings with { KeyBindings = new Dictionary<string, IReadOnlyList<string>>() };
        var overrides = new Dictionary<string, IReadOnlyList<string>>(Overrides(settings), StringComparer.Ordinal);
        overrides.Remove(commandId);
        return settings with { KeyBindings = overrides };
    }

    /// <summary>Stores a command's chords in display form ("Ctrl+B"); matching the defaults removes the override.</summary>
    private static void SetOverride(Dictionary<string, IReadOnlyList<string>> overrides, string commandId, IReadOnlyList<string> chords)
    {
        var defaults = Find(commandId)?.Defaults.Select(Normalize).OfType<string>().ToList() ?? [];
        if (chords.SequenceEqual(defaults, StringComparer.Ordinal)) overrides.Remove(commandId);
        else overrides[commandId] = chords.Select(Display).ToList();
    }
}
