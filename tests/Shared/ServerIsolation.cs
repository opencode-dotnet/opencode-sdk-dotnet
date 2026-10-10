using System.IO.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// Per-run home/state isolation for a spawned pinned server, in the shape of upstream's own test
/// fixtures (<c>packages/cli/test/fixture/environment.ts</c> and
/// <c>packages/core/script/test.ts</c> at the pin): every global root the server resolves comes
/// from these variables - the XDG roots and the home (<c>packages/util/src/global-roots.ts:4-8</c>)
/// and the temp root (<c>global-roots.ts:17</c>) - and the two variables that would otherwise
/// redirect state to the developer's real files are pinned explicitly rather than left to
/// inheritance (<c>OPENCODE_CONFIG_DIR</c> replaces the whole config root, <c>global.ts:79</c>;
/// <c>OPENCODE_DB</c> replaces the database path, <c>database-path.ts:6</c>). The config seed is
/// empty (upstream's hermetic posture), project configuration discovery is off, so nothing above a
/// location reaches the server, and the models catalog fetch is off: with both, no unregistered
/// outbound network rides the suite. A child inherits everything else from the runner, so this map
/// is the isolation boundary: a variable that can steer a server at the developer's data belongs
/// here, set, not merely absent. Every owned launch takes this one map over its own run root, so no
/// launch path carries a weaker boundary than another. The one test whose subject is project
/// configuration takes <see cref="ForProjectConfiguration"/>, the same map with discovery on.
/// </summary>
internal static class ServerIsolation
{
    /// <summary>Upstream's own switch for project configuration discovery.</summary>
    public const string ProjectConfigurationSwitch = "OPENCODE_DISABLE_PROJECT_CONFIG";

    /// <summary>Builds the isolation boundary for one server over its run root.</summary>
    /// <param name="fileSystem">The filesystem the run root is prepared and later checked through.</param>
    /// <param name="runRoot">The per-run root every global directory is redirected into.</param>
    /// <returns>The environment to launch with and the check the ready server must pass.</returns>
    public static IsolationBoundary For(IFileSystem fileSystem, string runRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(runRoot);

        var home = PrepareHome(fileSystem, runRoot);

        // The server's temp root is os.tmpdir()/opencode, which agents may use as an approved
        // external directory; under the inherited temp root it is shared with the developer's own
        // opencode. The runtime, and every tool the server spawns, takes its temp directory from
        // the same variables, and a tool may refuse one that does not exist.
        var temporary = fileSystem.Path.Combine(runRoot, "tmp");
        _ = fileSystem.Directory.CreateDirectory(temporary);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_DATA_HOME"] = fileSystem.Path.Combine(runRoot, "data"),
            ["XDG_CACHE_HOME"] = fileSystem.Path.Combine(runRoot, "cache"),
            ["XDG_CONFIG_HOME"] = fileSystem.Path.Combine(runRoot, "config"),
            ["XDG_STATE_HOME"] = fileSystem.Path.Combine(runRoot, "state"),
            ["TMPDIR"] = temporary,

            // The same directory XDG_CONFIG_HOME would resolve to, named explicitly: the variable
            // carries the whole config root, so a fixture that seeds a service config under
            // <config>/opencode/ and the daemon that reads it agree by construction.
            ["OPENCODE_CONFIG_DIR"] = fileSystem.Path.Combine(runRoot, "config", "opencode"),
            ["OPENCODE_DB"] = fileSystem.Path.Combine(runRoot, "data", "opencode.db"),
            ["OPENCODE_CONFIG_CONTENT"] = "{}",

            // An inherited explicit config file is a config source of its own that the seed above
            // does not replace (packages/cli/src/server-process.ts:119). Empty is absent at the
            // pin, where the path is taken only when truthy (packages/core/src/config/discovery.ts:65);
            // an owned file instead would hand every server one more config layer and one more
            // watch target (packages/core/src/config/watch.ts:18). The launcher's environment sets
            // a variable and cannot remove one, so absence itself is not expressible here.
            ["OPENCODE_CONFIG"] = string.Empty,
            ["OPENCODE_DISABLE_MODELS_FETCH"] = "1",

            // Configuration discovery otherwise walks from every location to the filesystem root
            // (packages/core/src/config/discovery.ts:33-36) and loads each ancestor's
            // opencode.json[c], .opencode directory, .claude and .agents skills and AGENTS.md - for
            // a request without a location, from the upstream checkout the server starts in, whose
            // own configuration declares a git reference the server clones. Off, only the isolated
            // global config dir and home are read (packages/cli/src/server-process.ts:116-118).
            [ProjectConfigurationSwitch] = "1",

            // Bun keeps its transpiler cache under XDG_CACHE_HOME unless this names another place,
            // so the isolated cache root above would make every source-run server transpile the
            // pinned monorepo from cold - several seconds per start. The cache is content-addressed
            // output of the pinned source, carries no state, and is shared across processes by
            // design, so one directory beside the run roots serves every fixture.
            ["BUN_RUNTIME_TRANSPILER_CACHE_PATH"] = fileSystem.Path.Combine(
                fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(runRoot)) ?? runRoot,
                "bun-transpiler-cache"),

            // global.home is OPENCODE_TEST_HOME before os.homedir() (packages/util/src/global.ts:17):
            // the home whose .claude and .agents directories config discovery reads and watches
            // (packages/core/src/config/discovery.ts:28-29). HOME is what os.homedir() answers
            // everywhere else the server asks (config/variable.ts:57, filesystem/protected.ts:4,
            // shell/parse.ts:322) and what the tools it spawns read.
            ["OPENCODE_TEST_HOME"] = home,
            ["HOME"] = home,
        };
        if (OperatingSystem.IsWindows())
        {
            MoveWindowsProfile(fileSystem, environment, home, temporary);
        }

        return new IsolationBoundary(fileSystem, environment);
    }

    /// <summary>
    /// Builds the boundary for a server whose subject is project configuration: the same map with
    /// discovery on, the way upstream's own config tests keep it on and put the project in a
    /// directory of their own. The switch is set to 0 rather than left out, because every child of
    /// the test session inherits it set to 1. Such a server's requests name a location inside its
    /// run root, whose chain <see cref="TestRunRootLocation"/> keeps clean.
    /// </summary>
    /// <param name="fileSystem">The filesystem the run root is prepared and later checked through.</param>
    /// <param name="runRoot">The per-run root every global directory is redirected into.</param>
    /// <returns>The environment to launch with and the check the ready server must pass.</returns>
    public static IsolationBoundary ForProjectConfiguration(IFileSystem fileSystem, string runRoot)
    {
        var isolation = For(fileSystem, runRoot);
        isolation.Environment[ProjectConfigurationSwitch] = "0";
        return isolation;
    }

    /// <summary>
    /// Creates the home. It is the one root the server never creates for itself, and service mode
    /// changes into it before it serves (packages/cli/src/server-process.ts:60 at the pin), so it
    /// exists before any launch rather than by each caller's care.
    /// </summary>
    private static string PrepareHome(IFileSystem fileSystem, string runRoot)
    {
        var home = fileSystem.Path.Combine(runRoot, "home");
        _ = fileSystem.Directory.CreateDirectory(home);

        // A terminal the server opens runs the user's own shell with this home. zsh answers a home
        // that has none of its startup files with an interactive first-run wizard, which waits for
        // a key and swallows whatever a test submits; an empty startup file is all it asks for.
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(home, ".zshrc"), string.Empty);
        return home;
    }

    /// <summary>
    /// Moves what Windows reads outside HOME. os.homedir() reads USERPROFILE there, and
    /// os.tmpdir() reads TEMP and TMP; upstream's own suite redirects all three
    /// (packages/core/script/test.ts:34).
    /// </summary>
    private static void MoveWindowsProfile(
        IFileSystem fileSystem, Dictionary<string, string> environment, string home, string temporary)
    {
        environment["USERPROFILE"] = home;
        environment["TMP"] = temporary;
        environment["TEMP"] = temporary;

        // The per-user stores Windows tools keep outside the home variables: the gcloud
        // configuration Vertex's Google auth library takes application default credentials
        // from, and every other tool's roaming and local profile data. They sit where Windows
        // puts them in a profile, and exist, because a tool may refuse a store that does not.
        var roaming = fileSystem.Path.Combine(home, "AppData", "Roaming");
        var local = fileSystem.Path.Combine(home, "AppData", "Local");
        _ = fileSystem.Directory.CreateDirectory(roaming);
        _ = fileSystem.Directory.CreateDirectory(local);
        environment["APPDATA"] = roaming;
        environment["LOCALAPPDATA"] = local;
    }
}
