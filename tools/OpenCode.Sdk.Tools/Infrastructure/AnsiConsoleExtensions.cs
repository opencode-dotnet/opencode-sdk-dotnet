using Spectre.Console;

namespace OpenCode.Sdk.Tools.Infrastructure;

internal static class AnsiConsoleExtensions
{
    private const int UnwrappedWidth = 4096;

    /// <summary>Keeps diagnostic lines whole in redirected logs, where the console defaults to 80 columns.</summary>
    public static void MarkupLineUnwrapped(this IAnsiConsole console, string markup)
    {
        var width = console.Profile.Width;
        console.Profile.Width = Math.Max(width, UnwrappedWidth);
        try
        {
            console.MarkupLine(markup);
        }
        finally
        {
            console.Profile.Width = width;
        }
    }
}
