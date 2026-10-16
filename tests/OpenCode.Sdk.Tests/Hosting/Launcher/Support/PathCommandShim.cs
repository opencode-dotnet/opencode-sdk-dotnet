using System.IO.Abstractions;
using System.Text;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>
/// An npm-shaped command shim on the process PATH: a temporary directory holding one Windows
/// batch file, prepended to PATH for the life of the instance and removed again with it. npm
/// never writes an <c>.exe</c> for a CLI package — it writes exactly this kind of shim — so this
/// is the arrangement the launcher's Windows resolution has to survive. The PATH mutation is
/// process-global, which is why every test using one carries the server-process constraint key.
/// </summary>
internal sealed class PathCommandShim : IDisposable
{
    private const string PathVariable = "PATH";
    private const string MarkerFile = "invoked.txt";

    private readonly TestRunRoot _root;
    private readonly string? _previousPath;

    private PathCommandShim(IFileSystem fileSystem, string fileName, Func<string, string> composeScript)
    {
        _root = new TestRunRoot(fileSystem);
        Directory = _root.CreateSubdirectory("shim");
        MarkerPath = fileSystem.Path.Combine(Directory, MarkerFile);
        fileSystem.File.WriteAllText(
            fileSystem.Path.Combine(Directory, fileName), composeScript(Directory), new UTF8Encoding(false));

        _previousPath = Environment.GetEnvironmentVariable(PathVariable);
        Environment.SetEnvironmentVariable(
            PathVariable, Directory + fileSystem.Path.PathSeparator + _previousPath);
    }

    /// <summary>Gets the directory prepended to PATH.</summary>
    public string Directory { get; }

    /// <summary>Gets the file a recording shim writes when it runs, and only then.</summary>
    public string MarkerPath { get; }

    /// <summary>A shim that hands everything it was given to another command, npm-style.</summary>
    public static PathCommandShim ForwardingTo(
        IFileSystem fileSystem, string fileName, IEnumerable<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var forwarded = new StringBuilder();
        foreach (var entry in command)
        {
            _ = forwarded.Append('"').Append(entry).Append("\" ");
        }

        // %* carries whatever the launcher appended — the caller's leading arguments plus
        // --stdio --port 0 — through to the forwarded command, exactly as an npm shim does.
        return new PathCommandShim(fileSystem, fileName, _ => Script(forwarded.Append("%*").ToString()));
    }

    /// <summary>A shim that records the fact it ran, so a test can prove it never did.</summary>
    public static PathCommandShim RecordingInvocation(IFileSystem fileSystem, string fileName) =>
        new(fileSystem, fileName, _ => Script("echo invoked>\"%~dp0" + MarkerFile + "\""));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathVariable, _previousPath);
        _root.Dispose();
    }

    /// <summary>
    /// Echo stays off: the launcher's readiness contract is the child's <em>first</em> stdout
    /// line, and a batch file that echoes its own commands would occupy it.
    /// </summary>
    private static string Script(string body) => "@echo off\r\n" + body + "\r\n";
}
