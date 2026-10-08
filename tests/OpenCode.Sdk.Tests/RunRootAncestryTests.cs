using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tests;

public sealed class RunRootAncestryTests
{
    [Test]
    public async Task FindDeveloperState_Should_Return_Null_For_A_Clean_Chain()
    {
        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);

        var found = new RunRootAncestry(fileSystem).FindDeveloperState(runs);

        await Assert.That(found).IsNull();
    }

    [Test]
    [Arguments(".git")]
    [Arguments(".opencode")]
    [Arguments(".claude/skills")]
    [Arguments(".agents/skills")]
    public async Task FindDeveloperState_Should_Name_A_Directory_The_Server_Would_Load_From_An_Ancestor(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);
        var state = fileSystem.Path.Combine([Ancestor(fileSystem), .. relative.Split('/')]);
        _ = fileSystem.Directory.CreateDirectory(state);

        var found = new RunRootAncestry(fileSystem).FindDeveloperState(runs);

        await Assert.That(found).IsEqualTo(state);
    }

    [Test]
    [Arguments("opencode.json")]
    [Arguments("opencode.jsonc")]
    public async Task FindDeveloperState_Should_Name_A_Project_Config_File_In_An_Ancestor(string name)
    {
        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);
        var state = fileSystem.Path.Combine(Ancestor(fileSystem), name);
        _ = fileSystem.Initialize().With(new FileDescription(state, "{}"));

        var found = new RunRootAncestry(fileSystem).FindDeveloperState(runs);

        await Assert.That(found).IsEqualTo(state);
    }

    [Test]
    public async Task FindDeveloperState_Should_Name_An_Enclosing_Git_Repository()
    {
        // A workspace inside a repository resolves to that repository's project
        // (packages/core/src/project.ts at the pin), so the developer's own checkout would become
        // the project of every plain workspace beneath it. A linked worktree marks itself with a
        // .git file rather than a directory, and both count.
        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);
        var marker = fileSystem.Path.Combine(Ancestor(fileSystem), ".git");
        _ = fileSystem.Initialize().With(new FileDescription(marker, "gitdir: elsewhere"));

        var found = new RunRootAncestry(fileSystem).FindDeveloperState(runs);

        await Assert.That(found).IsEqualTo(marker);
    }

    [Test]
    public async Task FindDeveloperState_Should_Ignore_A_Claude_Directory_Without_Skills()
    {
        // The compatibility roots contribute their skills directory and nothing else
        // (packages/core/src/config/plugin/compatibility.ts:41 at the pin).
        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);
        _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(Ancestor(fileSystem), ".claude"));

        var found = new RunRootAncestry(fileSystem).FindDeveloperState(runs);

        await Assert.That(found).IsNull();
    }

    [Test]
    [Arguments("opencode.json")]
    [Arguments("opencode.jsonc")]
    public async Task FindProjectConfiguration_Should_Name_A_Config_File_In_An_Ancestor(string name)
    {
        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);
        var configuration = fileSystem.Path.Combine(Ancestor(fileSystem), name);
        _ = fileSystem.Initialize().With(new FileDescription(configuration, "{}"));

        var found = new RunRootAncestry(fileSystem).FindProjectConfiguration(runs);

        await Assert.That(found).IsEqualTo(configuration);
    }

    [Test]
    public async Task FindProjectConfiguration_Should_Name_A_Project_Directory_In_An_Ancestor()
    {
        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);
        var configuration = fileSystem.Path.Combine(Ancestor(fileSystem), ".opencode");
        _ = fileSystem.Directory.CreateDirectory(configuration);

        var found = new RunRootAncestry(fileSystem).FindProjectConfiguration(runs);

        await Assert.That(found).IsEqualTo(configuration);
    }

    [Test]
    public async Task FindProjectConfiguration_Should_Ignore_Skills_And_Repositories()
    {
        // Skills are instructions, and a repository above is the checkout's own concern: neither
        // can start an MCP server, load a plugin, or add a tool.
        var fileSystem = new MockFileSystem();
        var runs = Runs(fileSystem);
        var ancestor = Ancestor(fileSystem);
        _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(ancestor, ".claude", "skills"));
        _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(ancestor, ".agents", "skills"));
        _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(ancestor, ".git"));

        var found = new RunRootAncestry(fileSystem).FindProjectConfiguration(runs);

        await Assert.That(found).IsNull();
    }

    /// <summary>MockFileSystem simulates the host OS, so the chain is built from its own temp root.</summary>
    private static string Ancestor(MockFileSystem fileSystem) =>
        fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "checkout");

    private static string Runs(MockFileSystem fileSystem)
    {
        var runs = fileSystem.Path.Combine(Ancestor(fileSystem), "artifacts", "test-runs");
        _ = fileSystem.Directory.CreateDirectory(runs);
        return runs;
    }
}
