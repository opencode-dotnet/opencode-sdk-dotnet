using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tests;

public sealed class ServerIsolationTests
{
    [Test]
    public async Task For_Should_Name_An_Existing_Home_Beneath_The_Run_Root()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");
        _ = fileSystem.Directory.CreateDirectory(runRoot);

        var environment = ServerIsolation.For(fileSystem, runRoot).Environment;

        // The two places an owned server takes its home from: upstream's own override
        // (packages/util/src/global.ts:17 at the pin) and the variable os.homedir() reads on this
        // platform. MockFileSystem simulates the host OS, so the platform branch matches it.
        _ = environment.TryGetValue("OPENCODE_TEST_HOME", out var home);
        await Assert.That(fileSystem.Directory.Exists(home)).IsTrue();
        await Assert.That(home is { } path
            && path.StartsWith(runRoot + fileSystem.Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsTrue();
        _ = environment.TryGetValue(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", out var platformHome);
        await Assert.That(platformHome).IsEqualTo(home);
    }

    /// <summary>
    /// Windows keeps per-user stores, gcloud's application default credentials among them, under
    /// two variables of their own; elsewhere they live under the home the map already moves.
    /// </summary>
    [Test]
    public async Task For_Should_Move_The_Windows_Per_User_Stores_Into_The_Home()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");

        var environment = ServerIsolation.For(fileSystem, runRoot).Environment;

        var home = environment["HOME"];
        _ = environment.TryGetValue("APPDATA", out var roaming);
        _ = environment.TryGetValue("LOCALAPPDATA", out var local);
        var windows = OperatingSystem.IsWindows();
        await Assert.That(roaming).IsEqualTo(windows ? fileSystem.Path.Combine(home, "AppData", "Roaming") : null);
        await Assert.That(local).IsEqualTo(windows ? fileSystem.Path.Combine(home, "AppData", "Local") : null);
        await Assert.That(new[] { roaming, local }.All(store => store is null || fileSystem.Directory.Exists(store))).IsTrue();
    }

    [Test]
    public async Task For_Should_Leave_An_Interactive_Shell_Nothing_To_Ask()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");

        _ = ServerIsolation.For(fileSystem, runRoot).Environment.TryGetValue("HOME", out var home);

        // A terminal the server opens runs the user's own shell in this home. zsh answers a home
        // without startup files with its first-run wizard, which waits for a key and swallows the
        // line a test submits.
        await Assert.That(home is { } path && fileSystem.File.Exists(fileSystem.Path.Combine(path, ".zshrc"))).IsTrue();
    }

    [Test]
    public async Task For_Should_Share_One_Transpiler_Cache_Outside_Every_Run_Root()
    {
        var fileSystem = new MockFileSystem();
        var runs = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "runs");
        var first = fileSystem.Path.Combine(runs, "first");
        var second = fileSystem.Path.Combine(runs, "second");

        _ = ServerIsolation.For(fileSystem, first).Environment.TryGetValue("BUN_RUNTIME_TRANSPILER_CACHE_PATH", out var firstCache);
        _ = ServerIsolation.For(fileSystem, second).Environment.TryGetValue("BUN_RUNTIME_TRANSPILER_CACHE_PATH", out var secondCache);

        // Bun keeps this cache under XDG_CACHE_HOME unless told otherwise, so an isolated cache root
        // makes every server start transpile the pinned source from cold.
        await Assert.That(firstCache).IsNotNull();
        await Assert.That(secondCache).IsEqualTo(firstCache);
        await Assert.That(firstCache is { } cache
            && !cache.StartsWith(first + fileSystem.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !cache.StartsWith(second + fileSystem.Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Project configuration discovery walks from a location to the drive root, so every owned
    /// server is launched with upstream's own switch for it on.
    /// </summary>
    [Test]
    public async Task For_Should_Turn_Project_Configuration_Discovery_Off()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");

        var environment = ServerIsolation.For(fileSystem, runRoot).Environment;

        await Assert.That(environment["OPENCODE_DISABLE_PROJECT_CONFIG"]).IsEqualTo("1");
    }

    /// <summary>
    /// The server whose subject is project configuration takes the same map with discovery on,
    /// set rather than left out, because every child of the session inherits it off.
    /// </summary>
    [Test]
    public async Task ForProjectConfiguration_Should_Differ_Only_In_Turning_Discovery_On()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");

        var isolated = ServerIsolation.For(fileSystem, runRoot).Environment;
        var discovering = ServerIsolation.ForProjectConfiguration(fileSystem, runRoot).Environment;

        await Assert.That(discovering["OPENCODE_DISABLE_PROJECT_CONFIG"]).IsEqualTo("0");
        await Assert.That(discovering.Keys).IsEquivalentTo(isolated.Keys);
        await Assert.That(discovering.Where(pair => pair.Key != "OPENCODE_DISABLE_PROJECT_CONFIG" && isolated[pair.Key] != pair.Value)).IsEmpty();
    }

    /// <summary>
    /// The server's temp root is the runtime's temp directory plus its own name, so the variables
    /// the runtime reads it from on this platform name an existing directory inside the run root.
    /// </summary>
    [Test]
    public async Task For_Should_Name_An_Existing_Temp_Directory_Beneath_The_Run_Root()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");

        var environment = ServerIsolation.For(fileSystem, runRoot).Environment;

        var temporary = environment["TMPDIR"];
        await Assert.That(fileSystem.Directory.Exists(temporary)).IsTrue();
        await Assert.That(temporary.StartsWith(runRoot + fileSystem.Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsTrue();
        _ = environment.TryGetValue("TMP", out var tmp);
        _ = environment.TryGetValue("TEMP", out var temp);
        var windows = OperatingSystem.IsWindows();
        await Assert.That(tmp).IsEqualTo(windows ? temporary : null);
        await Assert.That(temp).IsEqualTo(windows ? temporary : null);
    }

    [Test]
    public async Task ConfirmHonored_Should_Refuse_A_Server_That_Opened_No_Database_Under_The_Run_Root()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");
        var isolation = ServerIsolation.For(fileSystem, runRoot);

        var exception = await Assert.That(() => isolation.ConfirmHonored("The server under test")).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("The server under test");
        await Assert.That(exception.Message).Contains(isolation.Environment["XDG_DATA_HOME"]);
    }

    [Test]
    public async Task ConfirmHonored_Should_Accept_The_Database_Where_OPENCODE_DB_Points()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");
        var isolation = ServerIsolation.For(fileSystem, runRoot);
        _ = fileSystem.Initialize().With(new FileDescription(isolation.Environment["OPENCODE_DB"], string.Empty));

        await Assert.That(() => isolation.ConfirmHonored("The server under test")).ThrowsNothing();
    }

    /// <summary>
    /// A server that no longer reads OPENCODE_DB but still takes XDG_DATA_HOME keeps its database
    /// inside the run root, so it is isolated, and the check says so.
    /// </summary>
    [Test]
    public async Task ConfirmHonored_Should_Accept_A_Database_Anywhere_Under_The_Isolated_Data_Root()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");
        var isolation = ServerIsolation.For(fileSystem, runRoot);
        var database = fileSystem.Path.Combine(isolation.Environment["XDG_DATA_HOME"], "opencode", "opencode.db");
        _ = fileSystem.Initialize().With(new FileDescription(database, string.Empty));

        await Assert.That(() => isolation.ConfirmHonored("The server under test")).ThrowsNothing();
    }

    /// <summary>
    /// Windows' machine-wide configuration directory follows ProgramData, so each server gets one
    /// of its own inside the run root, existing and empty; elsewhere no variable moves it.
    /// </summary>
    [Test]
    public async Task For_Should_Give_A_Windows_Server_An_Empty_ProgramData_Of_Its_Own()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");

        var environment = ServerIsolation.For(fileSystem, runRoot).Environment;

        _ = environment.TryGetValue("ProgramData", out var programData);
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(programData).IsNull();
            return;
        }

        var expected = fileSystem.Path.Combine(runRoot, "programdata");
        await Assert.That(programData).IsEqualTo(expected);
        await Assert.That(fileSystem.Directory.Exists(expected)).IsTrue();
        await Assert.That(fileSystem.Directory.EnumerateFileSystemEntries(expected)).IsEmpty();
    }

    /// <summary>
    /// The sources no variable moves, in upstream's order at the pin
    /// (packages/core/src/config/discovery.ts:28-40): the system directory on Linux; on macOS the
    /// system directory and the per-user, then the machine, managed-preference plist.
    /// </summary>
    [Test]
    public async Task Unmovable_Should_Name_Upstreams_System_Sources_On_Linux_And_MacOS()
    {
        await Assert.That(SystemConfigurationSources.Unmovable(macOS: false, "dev")).IsEquivalentTo(["/etc/opencode"]);
        await Assert.That(SystemConfigurationSources.Unmovable(macOS: true, "dev")).IsEquivalentTo(
            [
                "/Library/Application Support/opencode",
                "/Library/Managed Preferences/dev/ai.opencode.managed.plist",
                "/Library/Managed Preferences/ai.opencode.managed.plist",
            ]);
    }

    /// <summary>
    /// Upstream loads a system directory that exists and a plist that is a file; the guard refuses
    /// either kind at any of the paths, so a file where a directory belongs is refused too.
    /// </summary>
    [Test]
    [Arguments(0, true)]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    public async Task FirstPresent_Should_Find_A_Source_That_Exists_As_A_Directory_Or_A_File(int index, bool asDirectory)
    {
        var fileSystem = new MockFileSystem();
        var candidates = SystemConfigurationSources.Unmovable(macOS: true, "dev");
        var present = candidates[index];
        if (asDirectory)
        {
            _ = fileSystem.Directory.CreateDirectory(present);
        }
        else
        {
            _ = fileSystem.Initialize().With(new FileDescription(present, "{}"));
        }

        await Assert.That(SystemConfigurationSources.FirstPresent(fileSystem, candidates)).IsEqualTo(present);
        await Assert.That(SystemConfigurationSources.FirstPresent(new MockFileSystem(), candidates)).IsNull();
    }

    /// <summary>
    /// On Linux and macOS an owned launch refuses to start while a source no variable moves exists,
    /// and names it; on Windows the same path is moved by ProgramData, so nothing is refused.
    /// </summary>
    [Test]
    public async Task For_Should_Refuse_A_Machine_Wide_Configuration_No_Variable_Moves()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "run");
        var present = SystemConfigurationSources.Unmovable(OperatingSystem.IsMacOS(), Environment.UserName)[0];
        _ = fileSystem.Directory.CreateDirectory(present);

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(() => ServerIsolation.For(fileSystem, runRoot)).ThrowsNothing();
            return;
        }

        var refusal = await Assert.That(() => ServerIsolation.For(fileSystem, runRoot)).Throws<InvalidOperationException>();
        await Assert.That(refusal!.Message).Contains("'" + present + "'");
    }
}
