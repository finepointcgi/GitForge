using GitForge.Core.Abstractions;
using Microsoft.JSInterop;

namespace GitForge.UI.Services;

/// <summary>Key event forwarded from gitforge.js.</summary>
public sealed record KeyInput(string Key, bool Ctrl, bool Shift, bool Alt, bool Meta, bool InInput);

/// <summary>
/// Keyboard-first command routing. gitforge.js captures only the chords bound here (it gets the list from
/// <see cref="ApplyAsync"/>) and forwards them; this service maps each chord to a command id that any component can
/// handle. Bindings come from <see cref="Keymap"/> plus the user's changes in settings.
/// </summary>
public sealed class ShortcutService : IAsyncDisposable
{
    private const int SequenceTimeoutMs = 600;

    private readonly IJSRuntime _js;
    private DotNetObjectReference<ShortcutService>? _ref;
    private Dictionary<string, string> _single = new(StringComparer.Ordinal);    // chord → command
    private Dictionary<string, string> _sequences = new(StringComparer.Ordinal); // "g g" → command
    private HashSet<string> _prefixes = new(StringComparer.Ordinal);             // first chords of sequences
    private List<JsBinding> _jsBindings = [];
    private string? _pending;
    private DateTime _pendingAt;

    public ShortcutService(IJSRuntime js)
    {
        _js = js;
        Configure(new AppSettings());
    }

    /// <summary>A chord gitforge.js should capture; <see cref="InText"/> lets it fire while a text field has focus.</summary>
    public sealed record JsBinding(string Chord, bool InText);

    public event Func<string, Task>? Command;

    /// <summary>Rebuilds the chord tables from <paramref name="settings"/>; single-key chords are dropped while vim-style navigation is off.</summary>
    public void Configure(AppSettings settings)
    {
        var single = new Dictionary<string, string>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in Keymap.Commands)
        {
            foreach (var chord in Keymap.Effective(settings, command.Id))
            {
                if (!settings.VimNavigation && Keymap.IsSingleKey(chord)) continue;
                var space = chord.IndexOf(' ', StringComparison.Ordinal);
                if (space < 0)
                {
                    single.TryAdd(chord, command.Id);
                }
                else if (sequences.TryAdd(chord, command.Id))
                {
                    prefixes.Add(chord[..space]);
                }
            }
        }

        _single = single;
        _sequences = sequences;
        _prefixes = prefixes;
        _jsBindings = single.Select(kv => new JsBinding(kv.Key, Keymap.WorksInTextFields(kv.Key, kv.Value)))
            .Concat(prefixes.Where(p => !single.ContainsKey(p)).Select(p => new JsBinding(p, false)))
            .ToList();
    }

    /// <summary>Applies changed settings and tells gitforge.js which chords to capture.</summary>
    public async Task ApplyAsync(AppSettings settings)
    {
        Configure(settings);
        if (_ref is not null) await _js.InvokeVoidAsync("gitforge.setKeyBindings", _jsBindings);
    }

    public async Task AttachAsync()
    {
        if (_ref is not null) return;
        _ref = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("gitforge.registerShortcuts", _ref, _jsBindings);
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
        var chord = Keymap.FromInput(k);
        if (chord is null) return null;

        if (_pending is { } first)
        {
            _pending = null;
            if (!k.InInput && (DateTime.UtcNow - _pendingAt).TotalMilliseconds < SequenceTimeoutMs && _sequences.TryGetValue($"{first} {chord}", out var sequence))
            {
                return sequence;
            }
        }

        if (_single.TryGetValue(chord, out var command))
        {
            return k.InInput && !Keymap.WorksInTextFields(chord, command) ? null : command;
        }

        if (!k.InInput && _prefixes.Contains(chord))
        {
            _pending = chord;
            _pendingAt = DateTime.UtcNow;
        }

        return null;
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
