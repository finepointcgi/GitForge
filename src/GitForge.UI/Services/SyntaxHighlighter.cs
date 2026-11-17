using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GitForge.Core.Diff;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace GitForge.UI.Services;

/// <summary>One colored run within a line.</summary>
public readonly record struct HighlightSpan(int Start, int Length, string? Color, bool Bold, bool Italic);

/// <summary>
/// TextMate-grammar syntax highlighting (the VS Code grammars and Dark+/Light+ themes, via TextMateSharp).
/// Grammars load lazily per language; tokenization is serialized because grammar instances are not thread-safe.
/// </summary>
public sealed partial class SyntaxHighlighter
{
    private const int MaxLineLength = 2000; // minified/generated lines are shown plain
    private static readonly TimeSpan LineTimeLimit = TimeSpan.FromMilliseconds(50);

    private readonly Lazy<Engine> _dark = new(() => new Engine(ThemeName.DarkPlus));
    private readonly Lazy<Engine> _light = new(() => new Engine(ThemeName.LightPlus));
    private readonly Lock _gate = new();

    [GeneratedRegex("^#[0-9A-Fa-f]{3,8}$")]
    private static partial Regex ColorPattern();

    private sealed class Engine
    {
        public Engine(ThemeName theme)
        {
            Options = new RegistryOptions(theme);
            Registry = new Registry(Options);
            Theme = Registry.GetTheme();
        }

        public RegistryOptions Options { get; }
        public Registry Registry { get; }
        public Theme Theme { get; }
        public ConcurrentDictionary<string, IGrammar?> Grammars { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A line-by-line tokenizer for one file in one theme, or null when the language is unknown.</summary>
    public LineTokenizer? CreateTokenizer(string path, bool dark)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        var engine = dark ? _dark.Value : _light.Value;
        var grammar = engine.Grammars.GetOrAdd(ext, e =>
        {
            try
            {
                lock (_gate)
                {
                    var scope = engine.Options.GetScopeByExtension(e);
                    return scope is null ? null : engine.Registry.LoadGrammar(scope);
                }
            }
            catch (Exception)
            {
                return null; // unknown or failing grammar: show plain text
            }
        });
        return grammar is null ? null : new LineTokenizer(this, grammar, engine.Theme);
    }

    /// <summary>Tokenizes successive lines of one stream, carrying multi-line state (comments, strings) forward.</summary>
    public sealed class LineTokenizer(SyntaxHighlighter owner, IGrammar grammar, Theme theme)
    {
        private IStateStack? _state;

        public IReadOnlyList<HighlightSpan> Next(string line)
        {
            if (line.Length == 0 || line.Length > MaxLineLength) return [];
            lock (owner._gate)
            {
                try
                {
                    var result = grammar.TokenizeLine(line, _state, LineTimeLimit);
                    _state = result.RuleStack;
                    var spans = new List<HighlightSpan>(result.Tokens.Length);
                    foreach (var token in result.Tokens)
                    {
                        var start = Math.Min(token.StartIndex, line.Length);
                        var end = Math.Min(token.EndIndex, line.Length);
                        if (end <= start) continue;
                        string? color = null;
                        var style = FontStyle.NotSet;
                        foreach (var rule in theme.Match(token.Scopes))
                        {
                            if (rule.foreground > 0) color = theme.GetColor(rule.foreground);
                            if (rule.fontStyle != FontStyle.NotSet) style = rule.fontStyle;
                        }

                        spans.Add(new HighlightSpan(start, end - start, color, style.HasFlag(FontStyle.Bold), style.HasFlag(FontStyle.Italic)));
                    }

                    return spans;
                }
                catch (Exception)
                {
                    _state = null; // a grammar error on one line must not break the view
                    return [];
                }
            }
        }
    }

    /// <summary>HTML for one line: every run HTML-encoded; colors validated before they reach a style attribute.</summary>
    public static string ToHtml(string line, IReadOnlyList<HighlightSpan>? spans)
    {
        if (spans is null || spans.Count == 0) return WebUtility.HtmlEncode(line);
        var sb = new StringBuilder(line.Length + (spans.Count * 32));
        var pos = 0;
        foreach (var s in spans)
        {
            if (s.Start > pos) sb.Append(WebUtility.HtmlEncode(line[pos..s.Start]));
            var text = WebUtility.HtmlEncode(line.Substring(s.Start, s.Length));
            var color = s.Color is { } c && ColorPattern().IsMatch(c) ? c : null;
            if (color is null && !s.Bold && !s.Italic)
            {
                sb.Append(text);
            }
            else
            {
                sb.Append("<span style=\"");
                if (color is not null) sb.Append("color:").Append(color).Append(';');
                if (s.Bold) sb.Append("font-weight:600;");
                if (s.Italic) sb.Append("font-style:italic;");
                sb.Append("\">").Append(text).Append("</span>");
            }

            pos = s.Start + s.Length;
        }

        if (pos < line.Length) sb.Append(WebUtility.HtmlEncode(line[pos..]));
        return sb.ToString();
    }
}

/// <summary>
/// Lazy highlighting for a diff. Each hunk has two streams (old side: context + deletions; new side: context + additions)
/// so multi-line constructs are tokenized correctly, and a stream only advances as far as the rows actually rendered.
/// </summary>
public sealed class DiffHighlighter
{
    private readonly SyntaxHighlighter _highlighter;
    private readonly FileDiff _diff;
    private readonly bool _dark;
    private readonly Dictionary<(int Hunk, bool NewSide), Stream> _streams = [];
    private readonly bool _enabled;

    public DiffHighlighter(SyntaxHighlighter highlighter, FileDiff diff, bool dark)
    {
        _highlighter = highlighter;
        _diff = diff;
        _dark = dark;
        _enabled = !diff.IsBinary && highlighter.CreateTokenizer(diff.Path, dark) is not null;
    }

    private sealed class Stream(SyntaxHighlighter.LineTokenizer tokenizer, List<int> lineIndexes)
    {
        public SyntaxHighlighter.LineTokenizer Tokenizer { get; } = tokenizer;
        public List<int> LineIndexes { get; } = lineIndexes;
        public Dictionary<int, IReadOnlyList<HighlightSpan>> Spans { get; } = [];
        public int Next { get; set; }
    }

    /// <summary>HTML for one diff line; the new side is used for context lines.</summary>
    public string Html(int hunkIndex, int lineIndex)
    {
        var line = _diff.Hunks[hunkIndex].Lines[lineIndex];
        if (!_enabled) return System.Net.WebUtility.HtmlEncode(line.DisplayText);
        var newSide = line.Kind != DiffLineKind.Deleted;
        return SyntaxHighlighter.ToHtml(line.DisplayText, Spans(hunkIndex, lineIndex, newSide));
    }

    private IReadOnlyList<HighlightSpan>? Spans(int hunkIndex, int lineIndex, bool newSide)
    {
        if (!_streams.TryGetValue((hunkIndex, newSide), out var stream))
        {
            var hunk = _diff.Hunks[hunkIndex];
            var indexes = Enumerable.Range(0, hunk.Lines.Count)
                .Where(i => hunk.Lines[i].Kind == DiffLineKind.Context || (hunk.Lines[i].Kind == DiffLineKind.Added) == newSide)
                .ToList();
            var tokenizer = _highlighter.CreateTokenizer(_diff.Path, _dark);
            if (tokenizer is null) return null;
            stream = new Stream(tokenizer, indexes);
            _streams[(hunkIndex, newSide)] = stream;
        }

        if (stream.Spans.TryGetValue(lineIndex, out var cached)) return cached;
        var lines = _diff.Hunks[hunkIndex].Lines;
        while (stream.Next < stream.LineIndexes.Count)
        {
            var i = stream.LineIndexes[stream.Next++];
            stream.Spans[i] = stream.Tokenizer.Next(lines[i].DisplayText);
            if (i == lineIndex) return stream.Spans[i];
        }

        return null;
    }
}
