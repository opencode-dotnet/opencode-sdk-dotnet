using Testably.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The isolation every child of the test host inherits unless its own launch sets something else.
/// An owned launch sets <see cref="ServerIsolation"/> over its own run root on top of this. What
/// this layer covers is every child that only inherits: a contender a door spawns, a fixture
/// process, or a server a test starts without a map of its own. Such a server finds opencode's own
/// config, data, state, cache and stored credentials under this session's root, not in the
/// developer's profile.
/// It is still a defect, because it shares that root with every other server like it and nothing
/// confirmed it honoured the isolation, so the session fails at its end when a database appeared
/// here. HOME and USERPROFILE stay as they are, and so do APPDATA and LOCALAPPDATA: git, the .NET
/// host and NuGet read them too, and the variables set here already move every root the server
/// itself derives from the home. What a provider's own SDK reads from the profile (an AWS shared
/// credentials file, gcloud's application default credentials) is not moved here; only an owned
/// launch, which takes the whole map, moves it.
/// </summary>
public static class SessionIsolation
{
    /// <summary>The variables the session leaves to the platform rather than to this layer.</summary>
    private static readonly string[] Inherited = ["HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA"];

    private static readonly RealFileSystem FileSystem = new();
    private static readonly HashSet<string> Applied = new(StringComparer.OrdinalIgnoreCase);
    private static TestRunRoot? _root;
    private static IsolationBoundary? _boundary;

    /// <summary>Gets the names this layer set on the test host, empty before it is applied.</summary>
    public static IReadOnlyCollection<string> AppliedNames => Applied;

    /// <summary>Gets the session's root, or null before the layer is applied.</summary>
    public static string? Root => _root?.Path;

    /// <summary>Sets the isolation on the test host's own environment, once, for every child it starts.</summary>
    internal static void Apply()
    {
        if (_root is not null)
        {
            return;
        }

        _root = new TestRunRoot(FileSystem);
        _boundary = ServerIsolation.For(FileSystem, _root.Path);
        foreach (var pair in _boundary.Environment)
        {
            // An empty value is how the map spells "absent" to a launcher that can only set
            // variables; here the variable can simply stay unset, which the scrub has made sure of.
            if (pair.Value.Length is 0 || Array.Exists(Inherited, name => string.Equals(name, pair.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            _ = Applied.Add(pair.Key);
        }
    }

    [After(TestSession)]
    public static void No_Server_Should_Have_Run_Without_Its_Own_Isolation()
    {
        if (_boundary?.FindDatabase() is { } database)
        {
            // The root stays: its log directory names the server that wrote here.
            throw new InvalidOperationException(
                "An opencode server opened its database at '" + database + "', under the isolation every child of "
                + "this test session inherits. It was started without ServerIsolation over a run root of its own and "
                + "without the check that it honoured it; give its launch both.");
        }

        _root?.Dispose();
    }
}
