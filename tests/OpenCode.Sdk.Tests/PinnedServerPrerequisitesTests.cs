using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The checkout the discovery guard requires, over a mock filesystem: each missing piece is named
/// with the command that fixes it, and a prepared checkout with the pinned Bun passes.
/// </summary>
public sealed class PinnedServerPrerequisitesTests
{
    private const string PinnedBun = "1.4.2";

    [Test]
    public async Task FindAsync_Should_Name_Only_The_Submodule_When_The_Pinned_Source_Is_Missing()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);

        var problems = await PinnedServerPrerequisites.FindAsync(fileSystem, root, PinnedBun, CancellationToken.None);

        await Assert.That(problems).HasSingleItem();
        await Assert.That(problems[0]).Contains("git submodule update --init --depth 1 external/opencode");
    }

    [Test]
    public async Task FindAsync_Should_Name_The_Install_When_The_Dependencies_Are_Missing()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, "{\"packageManager\":\"bun@" + PinnedBun + "\"}");

        var problems = await PinnedServerPrerequisites.FindAsync(fileSystem, root, PinnedBun, CancellationToken.None);

        await Assert.That(problems).HasSingleItem();
        await Assert.That(problems[0]).Contains("bun install --frozen-lockfile --ignore-scripts");
    }

    [Test]
    public async Task FindAsync_Should_Name_Both_Versions_When_The_Bun_On_Path_Is_Not_The_Pinned_One()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, "{\"packageManager\":\"bun@" + PinnedBun + "\"}");
        Install(fileSystem, root);

        var problems = await PinnedServerPrerequisites.FindAsync(fileSystem, root, "1.4.3", CancellationToken.None);

        await Assert.That(problems).HasSingleItem();
        await Assert.That(problems[0]).Contains("The bun on PATH is 1.4.3");
        await Assert.That(problems[0]).Contains("bun@" + PinnedBun);
    }

    [Test]
    public async Task FindAsync_Should_Name_The_Pinned_Bun_When_None_Is_On_Path()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, "{\"packageManager\":\"bun@" + PinnedBun + "\"}");
        Install(fileSystem, root);

        var problems = await PinnedServerPrerequisites.FindAsync(fileSystem, root, bunVersion: null, CancellationToken.None);

        await Assert.That(problems).HasSingleItem();
        await Assert.That(problems[0]).Contains("No bun is on PATH. Install bun " + PinnedBun);
    }

    [Test]
    public async Task FindAsync_Should_Refuse_A_Pin_That_Names_No_Bun()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, "{\"packageManager\":\"pnpm@9.0.0\"}");
        Install(fileSystem, root);

        var problems = await PinnedServerPrerequisites.FindAsync(fileSystem, root, PinnedBun, CancellationToken.None);

        await Assert.That(problems).HasSingleItem();
        await Assert.That(problems[0]).Contains("names no bun@ packageManager");
    }

    [Test]
    public async Task FindAsync_Should_Pass_A_Prepared_Checkout_With_The_Pinned_Bun()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, "{\"packageManager\":\"bun@" + PinnedBun + "\"}");
        Install(fileSystem, root);

        var problems = await PinnedServerPrerequisites.FindAsync(fileSystem, root, PinnedBun, CancellationToken.None);

        await Assert.That(problems).IsEmpty();
    }

    /// <summary>The one error a module reports names the module, the checkout, and every problem on its own line.</summary>
    [Test]
    public async Task Describe_Should_Name_The_Module_And_List_Every_Problem()
    {
        var message = PinnedServerPrerequisites.Describe("OpenCode.Sdk.Tests on .NET 10.0.0", "/repo", ["first", "second"]);

        await Assert.That(message).StartsWith("OpenCode.Sdk.Tests on .NET 10.0.0 cannot run: the pinned server its tests start is not prepared in '/repo'.");
        await Assert.That(message).EndsWith(Environment.NewLine + "  - first" + Environment.NewLine + "  - second");
    }

    private static string Root(MockFileSystem fileSystem) =>
        fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "repository");

    private static void CheckOut(MockFileSystem fileSystem, string root, string packageJson)
    {
        var submodule = PinnedServerPrerequisites.Submodule(fileSystem, root);
        _ = fileSystem.Initialize().With(
            new FileDescription(PinnedServerPrerequisites.Entry(fileSystem, submodule), string.Empty),
            new FileDescription(fileSystem.Path.Combine(submodule, "package.json"), packageJson));
    }

    private static void Install(MockFileSystem fileSystem, string root) =>
        _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(PinnedServerPrerequisites.Submodule(fileSystem, root), "node_modules"));
}
