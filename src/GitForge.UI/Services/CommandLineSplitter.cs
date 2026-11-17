using System.Text;

namespace GitForge.UI.Services;

/// <summary>Splits console input into argv using POSIX-like quoting ('single', "double", backslash escapes in double quotes).</summary>
public static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string input)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inArg = false;
        char quote = '\0';

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0';
                else current.Append(c);
                continue;
            }

            if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < input.Length && input[i + 1] is '"' or '\\') current.Append(input[++i]);
                else current.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    inArg = true;
                    break;
                case ' ' or '\t':
                    if (inArg) args.Add(current.ToString());
                    current.Clear();
                    inArg = false;
                    break;
                default:
                    current.Append(c);
                    inArg = true;
                    break;
            }
        }

        if (inArg) args.Add(current.ToString());
        return args;
    }
}
