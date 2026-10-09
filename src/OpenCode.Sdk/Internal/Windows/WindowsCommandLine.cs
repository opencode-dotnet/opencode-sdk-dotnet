namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The two Windows spellings of one resolved command, shared by the launcher and the
/// background-service contender. A batch shim never becomes the executable: the system cmd.exe
/// does, with the script as the first quoted token of the <see cref="BatchCommandLine"/> line, whose
/// outer quote pair is what <c>/s</c> strips, and whose metacharacter refusal throws before
/// anything spawns. Anything else is its argv through the MSVCRT quoting the child's runtime
/// parses back, so a path or an argument with spaces, quotes, or trailing backslashes survives the
/// round trip.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>Composes the executable and the command line for one resolved command.</summary>
    /// <param name="executable">The resolved command.</param>
    /// <param name="suppliedArguments">The caller's leading arguments; for a batch shim each one is screened.</param>
    /// <param name="launcherArguments">The SDK's own fixed arguments, after the supplied ones.</param>
    /// <returns>What runs, and its whole command line.</returns>
    /// <exception cref="OpenCodeServerException">A supplied argument carries a cmd metacharacter and the command is a batch shim.</exception>
    public static WindowsCommand For(
        ResolvedExecutable executable, IReadOnlyList<string> suppliedArguments, IReadOnlyList<string> launcherArguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(suppliedArguments);
        ArgumentNullException.ThrowIfNull(launcherArguments);

        if (executable.IsBatchScript)
        {
            var interpreter = BatchCommandLine.InterpreterPath;
            return new WindowsCommand(
                interpreter,
                "\"" + interpreter + "\" " + BatchCommandLine.Compose(executable.Path, suppliedArguments, launcherArguments));
        }

        // A path with a directory is what CreateProcessW starts, with no second search; a bare name
        // (only a caller that skipped the resolver passes one) is left to its search instead.
        var path = executable.Path;
        return new WindowsCommand(
            string.Equals(Path.GetFileName(path), path, StringComparison.Ordinal) ? null : path,
            ProcessArgumentComposer.Compose([path, .. suppliedArguments, .. launcherArguments]));
    }
}
