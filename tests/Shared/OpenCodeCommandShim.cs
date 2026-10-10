using System.IO.Abstractions;
using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// Writes an executable literally named <c>opencode</c> into a directory a test puts on PATH (or
/// names directly in <see cref="OpenCodeServerEnsureOptions.Command"/>): it forwards every argument
/// it receives to the pinned source-run serve — <c>bun &lt;cli entry&gt; &lt;args&gt;</c> — after
/// changing into the CLI package directory, so the launcher's default <c>opencode serve --service</c>
/// command resolves through the shipped PATH/PATHEXT search and starts the source-run daemon from
/// the working directory bun's workspace discovery needs. A <c>.cmd</c> batch shim on Windows, a
/// shell script elsewhere.
/// </summary>
internal static class OpenCodeCommandShim
{
    /// <summary>Gets the file the shim is written as: <c>opencode.cmd</c> on Windows, <c>opencode</c> elsewhere.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? "opencode.cmd" : "opencode";

    /// <summary>Writes the shim into <paramref name="directory"/> and returns its full path.</summary>
    /// <param name="fileSystem">The filesystem the pinned command is resolved through.</param>
    /// <param name="directory">The directory the shim is written into.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The absolute path of the shim file.</returns>
    public static async Task<string> WriteAsync(IFileSystem fileSystem, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var pinned = new PinnedServerCommand(fileSystem);
        var command = pinned.Resolve();
        var bun = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess())
            .Resolve(command[0])
            .Path;
        var entry = command[1];
        var cliDirectory = pinned.WorkingDirectory;

        var path = fileSystem.Path.Combine(directory, FileName);
        using var stream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(Compose(bun, entry, cliDirectory));
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
#if NET
        // A shell script the spawner runs directly must carry the execute bit; a Windows batch
        // shim routes through cmd.exe and needs none.
        if (!OperatingSystem.IsWindows())
        {
            fileSystem.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#endif
        return path;
    }

    /// <summary>
    /// Composes the shim body. The <c>cd</c> first is the one fact bun's workspace discovery cannot
    /// live without: it walks from the process working directory, not from the absolute entry path
    /// (the rationale the pinned server launch's working directory records), and the contender
    /// spawner sets no working directory of its own.
    /// </summary>
    private static string Compose(string bun, string entry, string cliDirectory) =>
        OperatingSystem.IsWindows()
            ? "@echo off\r\ncd /d \"" + cliDirectory + "\"\r\n\"" + bun + "\" \"" + entry + "\" %*\r\n"
            : "#!/bin/sh\ncd \"" + cliDirectory + "\" || exit 1\nexec \"" + bun + "\" \"" + entry + "\" \"$@\"\n";
}
