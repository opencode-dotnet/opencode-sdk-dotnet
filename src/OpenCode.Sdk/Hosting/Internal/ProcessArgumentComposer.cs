using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Composes a single <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> string for the
/// downlevel targets that lack <c>ArgumentList</c>, applying the MSVCRT parsing rules the modern
/// runtime's PasteArguments applies: quote when empty or containing space/tab/quote, double the
/// backslashes that precede a quote (including the closing one), and escape the quote itself.
/// </summary>
internal static class ProcessArgumentComposer
{
    public static string Compose(IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
            {
                _ = builder.Append(' ');
            }

            Append(builder, argument);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            _ = builder.Append(argument);
            return;
        }

        _ = builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                _ = builder.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }

            _ = builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        _ = builder.Append('\\', backslashes * 2).Append('"');
    }
}
