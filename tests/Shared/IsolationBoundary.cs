using System.IO.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// One owned server's isolation boundary: the environment every global root the server resolves
/// comes from (<see cref="ServerIsolation"/>), and the check that the server honoured it. The check
/// runs once the server is ready and before any test reaches it. A server that took the environment
/// opens its database under the run root; a run root without one means a server that reads and
/// writes the developer's own opencode profile. That is the failure an installed CLI would
/// produce silently if a later version stopped reading one of the variables, and the configuration-
/// writing tests would turn it into damage.
/// </summary>
internal sealed class IsolationBoundary
{
    private readonly IFileSystem _fileSystem;
    private readonly string _dataRoot;

    internal IsolationBoundary(IFileSystem fileSystem, Dictionary<string, string> environment)
    {
        _fileSystem = fileSystem;
        Environment = environment;
        _dataRoot = environment["XDG_DATA_HOME"];
    }

    /// <summary>Gets the environment the server is launched with.</summary>
    public Dictionary<string, string> Environment { get; }

    /// <summary>Confirms that a ready server opened its database under the isolated data root.</summary>
    /// <param name="server">What the server is, for the failure message.</param>
    /// <exception cref="InvalidOperationException">The server opened no database under the run root.</exception>
    public void ConfirmHonored(string server)
    {
        if (FindDatabase() is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            server + " is ready but opened no database under its isolated data root '" + _dataRoot
            + "': it is not honouring OPENCODE_DB or XDG_DATA_HOME and may be reading and writing the "
            + "developer's own opencode profile. No test runs against it.");
    }

    /// <summary>Finds a database a server opened under the isolated data root.</summary>
    /// <returns>The database's path, or null when there is none.</returns>
    public string? FindDatabase() =>
        _fileSystem.Directory.Exists(_dataRoot)
            ? _fileSystem.Directory.EnumerateFiles(_dataRoot, "opencode.db", SearchOption.AllDirectories).FirstOrDefault()
            : null;
}
