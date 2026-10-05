using System.Globalization;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Resolves a launcher command the way a shell would, before anything is spawned. The reason this
/// exists: <c>Process.Start</c> with <c>UseShellExecute=false</c> hands a bare name to Windows'
/// <c>CreateProcess</c>, which searches PATH but appends only <c>.exe</c> — never the PATHEXT
/// list. npm installs the opencode CLI as shim files (<c>opencode</c>, <c>opencode.cmd</c>,
/// <c>opencode.ps1</c>) and keeps the real binary inside <c>node_modules</c>, so the shipped
/// default failed on every npm-installed Windows machine with a bare Win32 error 2. Resolving
/// first also reports whether the result is a batch shim, which cannot be spawned directly with
/// any determinism — <see cref="OpenCodeServer"/> routes those through cmd.exe.
/// </summary>
internal sealed class ExecutableResolver
{
    /// <summary>The list Windows itself falls back to when PATHEXT is not set.</summary>
    private const string ConventionalExtensions = ".COM;.EXE;.BAT;.CMD";

    /// <summary>
    /// The single no-extension candidate: a name that already carries an extension, and every
    /// Unix name, is probed exactly as written.
    /// </summary>
    private static readonly string[] VerbatimOnly = [string.Empty];

    private readonly ExecutableSearchEnvironment _environment;

    public ExecutableResolver(ExecutableSearchEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _environment = environment;
    }

    /// <summary>Resolves one command to the path a spawn should name.</summary>
    /// <param name="command">The command as the caller spelled it.</param>
    /// <returns>The resolved target.</returns>
    /// <exception cref="OpenCodeServerException">A bare name matched nothing on PATH.</exception>
    public ResolvedExecutable Resolve(string command)
    {
        // A caller who wrote a path has already made the choice: no search, and no existence
        // probe either — the spawn itself reports a wrong path better than a guess here could.
        // On Unix a relative path is made absolute here, against this process's directory: the
        // child may start in another directory, and a relative path would then name a file there.
        if (CarriesItsOwnPath(command))
        {
            return Describe(
                command,
                _environment.IsWindows || IsRooted(command) ? command : Combine(_environment.CurrentDirectory, command));
        }

        var directories = SearchDirectories();
        var extensions = CandidateExtensions(command);
        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                var candidate = Combine(directory, command + extension);
                if (_environment.FileExists(candidate))
                {
                    return Describe(command, candidate);
                }
            }
        }

        throw NotFound(command, directories.Count, extensions);
    }

    private static bool IsBatchScript(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the name already carries an extension, which is probed as written instead of
    /// gaining the PATHEXT list. A dot at the very front is a name, not an extension.
    /// </summary>
    private static bool HasExtension(string command) =>
        command.Length > 1 && command.AsSpan(1).IndexOf('.') >= 0;

    /// <summary>Names the extensions that were actually appended, and nothing when none were.</summary>
    private static string DescribeExtensions(string[] extensions) =>
        extensions is [""] ? string.Empty : " with the extensions " + string.Join(", ", extensions);

    private static OpenCodeServerException NotFound(string command, int directoryCount, string[] extensions) =>
        new($"The server command '{command}' was not found on PATH: " +
            $"{directoryCount.ToString(CultureInfo.InvariantCulture)} " +
            $"{(directoryCount == 1 ? "directory" : "directories")} searched{DescribeExtensions(extensions)}. " +
            "Pass the executable's full path in OpenCodeServerOptions.Command, or install the CLI " +
            "package @opencode/cli.");

    private ResolvedExecutable Describe(string command, string path) =>
        new(command, path, _environment.IsWindows && IsBatchScript(path));

    /// <summary>
    /// Whether the command already names a location rather than asking to be found. A backslash
    /// counts only on Windows, where it is a separator; a drive-relative <c>C:opencode</c> names
    /// a location too, which is why the colon is checked alongside.
    /// </summary>
    private bool CarriesItsOwnPath(string command)
    {
        foreach (var character in command)
        {
            if (character == '/' || (_environment.IsWindows && character == '\\'))
            {
                return true;
            }
        }

        return _environment.IsWindows && command.Length >= 2 && command[1] == ':';
    }

    private List<string> SearchDirectories()
    {
        if (_environment.SearchPath is not { Length: > 0 } searchPath)
        {
            return [];
        }

        var directories = new List<string>();
        foreach (var entry in searchPath.Split(_environment.IsWindows ? ';' : ':'))
        {
            // An empty entry is the shell's own "skip me", and a relative one means to a shell
            // what it means here: a directory under the process's current directory.
            if (entry.Length != 0)
            {
                directories.Add(IsRooted(entry) ? entry : Combine(_environment.CurrentDirectory, entry));
            }
        }

        return directories;
    }

    private string[] CandidateExtensions(string command)
    {
        if (!_environment.IsWindows || HasExtension(command))
        {
            return VerbatimOnly;
        }

        var list = ConventionalExtensions;
        if (_environment.SearchExtensions is { } configured && !string.IsNullOrWhiteSpace(configured))
        {
            list = configured;
        }

        return list.Split([';'], StringSplitOptions.RemoveEmptyEntries);
    }

    private bool IsRooted(string entry)
    {
        if (entry[0] == '/' || (_environment.IsWindows && entry[0] == '\\'))
        {
            return true;
        }

        return _environment.IsWindows && entry.Length >= 2 && entry[1] == ':';
    }

    private string Combine(string directory, string name)
    {
        if (directory.Length == 0)
        {
            return name;
        }

        var last = directory[^1];
        if (last == '/' || (_environment.IsWindows && last == '\\'))
        {
            return directory + name;
        }

        return directory + (_environment.IsWindows ? "\\" : "/") + name;
    }
}
