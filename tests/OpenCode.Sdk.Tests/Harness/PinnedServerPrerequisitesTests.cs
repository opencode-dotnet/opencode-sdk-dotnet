using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The checkout the discovery guard requires, over a mock filesystem: each missing piece is named
/// with the command that fixes it, a prepared checkout with the pinned Bun passes, and another Bun
/// passes only through the explicit opt-in, which names both versions.
/// </summary>
public sealed class PinnedServerPrerequisitesTests
{
    private const string PinnedBun = "1.4.2";
    private const string OtherBun = "1.4.3";
    private const string RecordedCommit = "1111111111111111111111111111111111111111";
    private const string OtherCommit = "2222222222222222222222222222222222222222";
    private const string PinnedPackageJson = "{\"packageManager\":\"bun@" + PinnedBun + "\"}";

    [Test]
    public async Task FindAsync_Should_Name_Only_The_Submodule_When_The_Pinned_Source_Is_Missing()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, PinnedBun, bunMismatch: null);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0]).Contains("git submodule update --init --depth 1 external/opencode");
    }

    [Test]
    public async Task FindAsync_Should_Name_The_Install_When_The_Dependencies_Are_Missing()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, PinnedPackageJson);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, PinnedBun, bunMismatch: null);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0]).Contains("bun install --frozen-lockfile --ignore-scripts");
    }

    [Test]
    public async Task FindAsync_Should_Name_Both_Versions_When_The_Bun_On_Path_Is_Not_The_Pinned_One()
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, OtherBun, bunMismatch: null);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0]).Contains("The bun on PATH is " + OtherBun);
        await Assert.That(readiness.Problems[0]).Contains("bun@" + PinnedBun);
        await Assert.That(readiness.Problems[0]).Contains("set OPENCODE_SDK_TESTS_BUN_MISMATCH=allow");
        await Assert.That(readiness.AllowedMismatch).IsNull();
    }

    [Test]
    public async Task FindAsync_Should_Name_The_Pinned_Bun_When_None_Is_On_Path()
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, bunVersion: null, bunMismatch: null);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0]).Contains("No bun is on PATH. Install bun " + PinnedBun);
    }

    [Test]
    public async Task FindAsync_Should_Refuse_A_Pin_That_Names_No_Bun()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, "{\"packageManager\":\"pnpm@9.0.0\"}");
        Install(fileSystem, root);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, PinnedBun, bunMismatch: null);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0]).Contains("names no bun@ packageManager");
    }

    [Test]
    public async Task FindAsync_Should_Pass_A_Prepared_Checkout_With_The_Pinned_Bun()
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, PinnedBun, bunMismatch: null);

        await Assert.That(readiness.Problems).IsEmpty();
        await Assert.That(readiness.AllowedMismatch).IsNull();
    }

    [Test]
    public async Task FindAsync_Should_Allow_And_Report_Another_Bun_When_The_Mismatch_Is_Allowed()
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(
            fileSystem, root, OtherBun, PinnedServerPrerequisites.AllowBunMismatch);

        await Assert.That(readiness.Problems).IsEmpty();
        await Assert.That(readiness.AllowedMismatch)
            .IsEqualTo($"pinned bun {PinnedBun}, running bun {OtherBun} (OPENCODE_SDK_TESTS_BUN_MISMATCH=allow)");
    }

    /// <summary>The opt-in allows a version, not an absence: with no Bun there is nothing to run the server.</summary>
    [Test]
    public async Task FindAsync_Should_Refuse_No_Bun_On_Path_When_The_Mismatch_Is_Allowed()
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(
            fileSystem, root, bunVersion: null, PinnedServerPrerequisites.AllowBunMismatch);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0]).Contains("No bun is on PATH. Install bun " + PinnedBun);
        await Assert.That(readiness.AllowedMismatch).IsNull();
    }

    [Test]
    public async Task FindAsync_Should_Still_Name_The_Install_When_The_Mismatch_Is_Allowed()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, PinnedPackageJson);

        var readiness = await FindWithMatchingCommitsAsync(
            fileSystem, root, OtherBun, PinnedServerPrerequisites.AllowBunMismatch);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0]).Contains("bun install --frozen-lockfile --ignore-scripts");
    }

    [Test]
    public async Task FindAsync_Should_Report_Nothing_When_The_Mismatch_Is_Allowed_But_The_Pinned_Bun_Runs()
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(
            fileSystem, root, PinnedBun, PinnedServerPrerequisites.AllowBunMismatch);

        await Assert.That(readiness.Problems).IsEmpty();
        await Assert.That(readiness.AllowedMismatch).IsNull();
    }

    /// <summary>
    /// Any value but the one the variable takes is refused, even with the pinned Bun on PATH: a typo
    /// would otherwise read as an opt-in that silently does nothing until the day the versions part.
    /// </summary>
    [Test]
    [Arguments("Allow")]
    [Arguments("true")]
    [Arguments("1")]
    public async Task FindAsync_Should_Refuse_Any_Other_Value_Of_The_Mismatch_Variable(string value)
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, PinnedBun, value);

        await Assert.That(readiness.Problems).HasSingleItem();
        await Assert.That(readiness.Problems[0])
            .IsEqualTo($"OPENCODE_SDK_TESTS_BUN_MISMATCH is '{value}', but the only value it takes is 'allow'. Unset it to require the pinned bun.");
    }

    [Test]
    public async Task FindAsync_Should_Refuse_Another_Bun_When_The_Mismatch_Variable_Has_Another_Value()
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, OtherBun, "yes");

        await Assert.That(readiness.Problems).Count().IsEqualTo(2);
        await Assert.That(readiness.Problems[0]).Contains("the only value it takes is 'allow'");
        await Assert.That(readiness.Problems[1]).Contains("The bun on PATH is " + OtherBun);
        await Assert.That(readiness.AllowedMismatch).IsNull();
    }

    /// <summary>The one error a module reports names the module, the checkout, and every problem on its own line.</summary>
    [Test]
    public async Task Describe_Should_Name_The_Module_And_List_Every_Problem()
    {
        var message = PinnedServerPrerequisites.Describe("OpenCode.Sdk.Tests on .NET 10.0.0", "/repo", ["first", "second"]);

        await Assert.That(message).StartsWith("OpenCode.Sdk.Tests on .NET 10.0.0 cannot run: the pinned server its tests start is not prepared in '/repo'.");
        await Assert.That(message).EndsWith(Environment.NewLine + "  - first" + Environment.NewLine + "  - second");
    }

    [Test]
    public async Task FindAsync_Should_Name_A_Wrong_Checkout_Before_A_Missing_Install()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, PinnedPackageJson);
        var submodule = fileSystem.Path.Combine(root, "external", "opencode");

        var readiness = await PinnedServerPrerequisites.FindAsync(
            fileSystem, root, PinnedBun, bunMismatch: null, RecordedCommit, OtherCommit, CancellationToken.None);

        await Assert.That(readiness.Problems).IsEquivalentTo([
            $"The pin's checkout at '{submodule}' is 2222222222222222222222222222222222222222, but this repository records 1111111111111111111111111111111111111111. Run: git submodule update --init --depth 1 external/opencode",
            $"The pinned server dependencies are not installed under '{submodule}'. Run there: bun install --frozen-lockfile --ignore-scripts",
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(null, RecordedCommit, "recorded gitlink")]
    [Arguments(RecordedCommit, null, "checked-out HEAD")]
    public async Task FindAsync_Should_Refuse_An_Unreadable_Commit(string? recorded, string? checkedOut, string unreadable)
    {
        var fileSystem = new MockFileSystem();
        var root = Prepared(fileSystem);
        var submodule = fileSystem.Path.Combine(root, "external", "opencode");

        var readiness = await PinnedServerPrerequisites.FindAsync(
            fileSystem, root, PinnedBun, bunMismatch: null, recorded, checkedOut, CancellationToken.None);

        await Assert.That(readiness.Problems).IsEquivalentTo(
            [$"Cannot read the pin's {unreadable} at '{submodule}'. Run: git submodule update --init --depth 1 external/opencode"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FindAsync_Should_Not_Add_Commit_Problems_When_Source_Is_Missing()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        var entry = fileSystem.Path.Combine(root, "external", "opencode", "packages", "cli", "src", "index.ts");

        var readiness = await PinnedServerPrerequisites.FindAsync(
            fileSystem, root, PinnedBun, bunMismatch: null, recordedCommit: null, checkedOutCommit: null, CancellationToken.None);

        await Assert.That(readiness.Problems).IsEquivalentTo(
            [$"The pinned server source is missing at '{entry}'. Run: git submodule update --init --depth 1 external/opencode"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FindAsync_Should_Name_A_Mismatch_Typo_Before_Missing_Source()
    {
        var fileSystem = new MockFileSystem();
        var root = Root(fileSystem);
        var entry = fileSystem.Path.Combine(root, "external", "opencode", "packages", "cli", "src", "index.ts");

        var readiness = await FindWithMatchingCommitsAsync(fileSystem, root, PinnedBun, "Allow");

        await Assert.That(readiness.Problems).IsEquivalentTo([
            "OPENCODE_SDK_TESTS_BUN_MISMATCH is 'Allow', but the only value it takes is 'allow'. Unset it to require the pinned bun.",
            $"The pinned server source is missing at '{entry}'. Run: git submodule update --init --depth 1 external/opencode",
        ], CollectionOrdering.Matching);
    }

    private static Task<PinnedServerPrerequisites.Readiness> FindWithMatchingCommitsAsync(
        MockFileSystem fileSystem, string root, string? bunVersion, string? bunMismatch) =>
        PinnedServerPrerequisites.FindAsync(fileSystem, root, bunVersion, bunMismatch, RecordedCommit, RecordedCommit, CancellationToken.None);

    private static string Root(MockFileSystem fileSystem) =>
        fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "repository");

    /// <summary>A checkout pinned to <see cref="PinnedBun"/> with its dependencies installed.</summary>
    private static string Prepared(MockFileSystem fileSystem)
    {
        var root = Root(fileSystem);
        CheckOut(fileSystem, root, PinnedPackageJson);
        Install(fileSystem, root);
        return root;
    }

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
