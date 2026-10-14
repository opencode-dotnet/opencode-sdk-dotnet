#if NET
using System.Buffers;
#endif
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Launches a Windows batch target the deterministic way: explicitly through the system cmd.exe
/// rather than through <c>CreateProcess</c>'s implicit batch handling. cmd.exe re-parses the line
/// it is handed, so composition here is the whole security boundary — the same one Rust and Node
/// drew for BatBadBut (CVE-2024-24576), and with the same fail-closed answer: an argument carrying
/// a cmd metacharacter is refused, never escaped.
/// </summary>
internal static class BatchCommandLine
{
    /// <summary>
    /// The characters cmd.exe acts on rather than passes through. Quoting does not neutralize
    /// them reliably (<c>%</c> still expands inside quotes, and a quote of its own re-splits the
    /// line), which is why the answer is refusal rather than escaping.
    /// </summary>
#if NET
    private static readonly SearchValues<char> Metacharacters = SearchValues.Create("&|<>^%!\"\r\n");
#else
    private static readonly char[] Metacharacters = ['&', '|', '<', '>', '^', '%', '!', '"', '\r', '\n'];
#endif

    /// <summary>
    /// Gets the absolute path to the system cmd.exe, rather than a bare "cmd" resolved through
    /// PATH: the interpreter is pinned to its well-known system location, the same way the
    /// downlevel Windows tree kill pins taskkill.
    /// </summary>
    public static string InterpreterPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>
    /// Composes the whole cmd.exe command line for one batch target.
    /// </summary>
    /// <param name="script">The resolved <c>.cmd</c>/<c>.bat</c> path.</param>
    /// <param name="suppliedArguments">The caller's leading arguments; every one is screened.</param>
    /// <param name="launcherArguments">The launcher's own fixed arguments, which carry no metacharacters.</param>
    /// <returns>The argument string for cmd.exe.</returns>
    /// <exception cref="OpenCodeServerException">A supplied argument carries a cmd metacharacter.</exception>
    public static string Compose(
        string script,
        IReadOnlyList<string> suppliedArguments,
        IReadOnlyList<string> launcherArguments)
    {
        // Screened before a single character is composed: nothing is spawned, and nothing is
        // half-built, when an argument is refused.
        foreach (var argument in suppliedArguments)
        {
            Refuse(script, argument);
        }

        // /d skips AutoRun commands from the registry, so a machine-local profile cannot inject
        // itself into the launch. /s plus the surrounding quote pair is the one documented,
        // deterministic parse: cmd strips exactly the first and last quote after /c and takes the
        // rest verbatim, which leaves each inner quoted token intact.
        var line = new StringBuilder("/d /s /c \"");
        Quote(line, script);
        foreach (var argument in suppliedArguments)
        {
            Quote(line.Append(' '), argument);
        }

        foreach (var argument in launcherArguments)
        {
            Quote(line.Append(' '), argument);
        }

        return line.Append('"').ToString();
    }

    private static bool CarriesMetacharacter(string argument) =>
#if NET
        argument.AsSpan().ContainsAny(Metacharacters);
#else
        argument.IndexOfAny(Metacharacters) >= 0;
#endif

    private static void Quote(StringBuilder line, string value) =>
        line.Append('"').Append(value).Append('"');

    private static void Refuse(string script, string argument)
    {
        if (!CarriesMetacharacter(argument))
        {
            return;
        }

        throw new OpenCodeServerException(
            $"The server command resolved to the batch shim '{script}', which runs through " +
            $"cmd.exe, and cmd.exe re-parses its command line — so the argument '{argument}' is " +
            "refused rather than escaped: it contains one of & | < > ^ % ! \" or a line break. " +
            "Remove the character, or point OpenCodeServerOptions.Command at the real executable " +
            "instead of the shim.");
    }
}
