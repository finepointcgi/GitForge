using GitForge.Core.Abstractions;
using GitForge.Platform.Settings;
using GitForge.UI.Services;
using Microsoft.JSInterop;

namespace GitForge.UI.Tests;

public class KeymapTests
{
    private static KeyInput Key(string key, bool ctrl = false, bool shift = false, bool alt = false, bool inInput = false) =>
        new(key, ctrl, shift, alt, Meta: false, inInput);

    [Fact]
    public void EveryDefault_IsValid_RoundTripsThroughDisplay_AndIsUsedOnce()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in Keymap.Commands)
        {
            foreach (var text in command.Defaults)
            {
                var chord = Keymap.Normalize(text);
                Assert.True(chord is not null, $"{command.Id}: '{text}' is not a valid chord");
                Assert.Equal(chord, Keymap.Normalize(Keymap.Display(chord!)));
                Assert.True(seen.TryAdd(chord!, command.Id), $"{text} is bound to both {seen.GetValueOrDefault(chord!)} and {command.Id}");
            }
        }

        // No default hides another: a single chord never equals the first step of a sequence.
        Assert.All(seen.Keys.Where(k => k.Contains(' ', StringComparison.Ordinal)), seq => Assert.False(seen.ContainsKey(seq.Split(' ')[0])));
        Assert.Equal(Keymap.Commands.Count, Keymap.Commands.Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("p", true, false, "ctrl+p")]
    [InlineData("P", true, true, "ctrl+shift+p")]
    [InlineData("G", false, true, "shift+g")]      // letters keep Shift
    [InlineData("?", false, true, "?")]            // "?" already means Shift+/
    [InlineData(" ", false, true, "space")]
    [InlineData("ArrowDown", false, true, "shift+arrowdown")]
    [InlineData("F5", false, false, "f5")]
    [InlineData("Escape", false, false, "escape")]
    [InlineData("`", true, false, "ctrl+`")]
    public void FromInput_MatchesTheCanonicalChord(string key, bool ctrl, bool shift, string expected) =>
        Assert.Equal(expected, Keymap.FromInput(Key(key, ctrl, shift)));

    [Theory]
    [InlineData("Control")]
    [InlineData("Shift")]
    [InlineData("Dead")]
    [InlineData("\u0001")]
    public void FromInput_IgnoresKeysThatAreNotChords(string key) => Assert.Null(Keymap.FromInput(Key(key, ctrl: true)));

    [Theory]
    [InlineData("Ctrl+Shift+F", "ctrl+shift+f", "Ctrl+Shift+F")]
    [InlineData("cmd+p", "ctrl+p", "Ctrl+P")]
    [InlineData("Shift+↓", "shift+arrowdown", "Shift+↓")]
    [InlineData("g g", "g g", "g g")]
    [InlineData("Ctrl++", "ctrl++", "Ctrl++")]
    [InlineData("Ctrl+PgDn", "ctrl+pagedown", "Ctrl+PgDn")]
    public void Normalize_AndDisplay(string text, string chord, string display)
    {
        Assert.Equal(chord, Keymap.Normalize(text));
        Assert.Equal(display, Keymap.Display(chord));
    }

    [Theory]
    [InlineData("Hyper+X")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("a b c")]
    [InlineData("")]
    public void Normalize_RejectsInvalidText(string text) => Assert.Null(Keymap.Normalize(text));

    [Fact]
    public void WithBinding_MovesAChordFromItsOwner_AndResetRestoresDefaults()
    {
        var settings = new AppSettings();
        Assert.Equal("branch.new", Keymap.OwnerOf(settings, "ctrl+b"));

        var moved = Keymap.WithBinding(settings, "remote.fetch", "ctrl+b");

        Assert.Equal(["ctrl+shift+f", "ctrl+b"], Keymap.Effective(moved, "remote.fetch"));
        Assert.Empty(Keymap.Effective(moved, "branch.new"));
        Assert.Equal(["Ctrl+Shift+F", "Ctrl+B"], moved.KeyBindings["remote.fetch"]); // stored readable
        Assert.Null(Keymap.Label(moved, "branch.new"));
        Assert.Equal(string.Empty, Keymap.Hint(moved, "branch.new"));

        var reset = Keymap.Reset(Keymap.Reset(moved, "remote.fetch"), "branch.new");
        Assert.Empty(reset.KeyBindings);
        Assert.Equal("Ctrl+B", Keymap.Label(reset, "branch.new"));
    }

    [Fact]
    public void WithBinding_ASequencePrefix_TakesTheSequenceAway()
    {
        var settings = Keymap.WithBinding(new AppSettings(), "repo.refresh", "g");

        Assert.Empty(Keymap.Effective(settings, "nav.top"));          // "g g" would never fire after "g" was taken
        Assert.Equal(["f5", "g"], Keymap.Effective(settings, "repo.refresh"));
    }

    [Fact]
    public void RemovingADefault_ThenAddingItBack_LeavesNoOverride()
    {
        var removed = Keymap.WithoutBinding(new AppSettings(), "nav.down", "j");
        Assert.Equal(["arrowdown"], Keymap.Effective(removed, "nav.down"));

        var restored = Keymap.WithBinding(Keymap.WithoutBinding(removed, "nav.down", "arrowdown"), "nav.down", "j");
        restored = Keymap.WithBinding(restored, "nav.down", "arrowdown");
        Assert.Empty(restored.KeyBindings);
    }

    [Fact]
    public void ReservedChords_HaveAReason() => Assert.All(["tab", "ctrl+c", "ctrl+v", "alt+f4"], c => Assert.NotNull(Keymap.ReservedReason(c)));

    [Fact]
    public async Task CustomBindings_SurviveASettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitforge-keys-{Guid.NewGuid():N}.json");
        try
        {
            using (var store = new JsonSettingsStore(path))
            {
                await store.LoadAsync();
                await store.UpdateAsync(s => Keymap.WithBinding(s, "remote.fetch", "ctrl+alt+f"));
            }

            using var reopened = new JsonSettingsStore(path);
            var settings = await reopened.LoadAsync();
            Assert.Equal(["ctrl+shift+f", "ctrl+alt+f"], Keymap.Effective(settings, "remote.fetch"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShortcutService_RoutesChords_ByContext()
    {
        var service = new ShortcutService(new NoJs());

        Assert.Equal("palette.open", service.Resolve(Key("p", ctrl: true)));
        Assert.Equal("commit.submit", service.Resolve(Key("Enter", ctrl: true, inInput: true))); // Ctrl chords work while typing
        Assert.Null(service.Resolve(Key("z", ctrl: true, inInput: true)));                      // ...except undo, which stays native
        Assert.Equal("edit.undo", service.Resolve(Key("z", ctrl: true)));
        Assert.Null(service.Resolve(Key("j", inInput: true)));                                    // typing a "j" is just typing
        Assert.Equal("nav.down", service.Resolve(Key("j")));
        Assert.Equal("select.down", service.Resolve(Key("ArrowDown", shift: true)));
        Assert.Null(service.Resolve(Key("g")));                                                   // first half of "g g"
        Assert.Equal("nav.top", service.Resolve(Key("g")));
    }

    [Fact]
    public void ShortcutService_HonorsCustomBindings_AndVimToggle()
    {
        var service = new ShortcutService(new NoJs());
        service.Configure(Keymap.WithBinding(new AppSettings { VimNavigation = false }, "remote.fetch", "ctrl+alt+f"));

        Assert.Equal("remote.fetch", service.Resolve(Key("f", ctrl: true, alt: true)));
        Assert.Null(service.Resolve(Key("j")));                                                   // single keys are off
        Assert.Equal("nav.down", service.Resolve(Key("ArrowDown")));                              // named keys still work
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("No JS in unit tests.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("No JS in unit tests.");
    }
}
