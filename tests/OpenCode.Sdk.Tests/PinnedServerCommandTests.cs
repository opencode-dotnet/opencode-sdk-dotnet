using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The working directory every pinned server that is not in service mode starts in, over a mock
/// filesystem that mirrors where this test assembly really sits, so the repository root is found
/// the way a real launch finds it.
/// </summary>
public sealed class PinnedServerCommandTests
{
    private static readonly string RepositoryRoot = new PinnedServerCommand(new RealFileSystem()).RepositoryRoot;

    [Test]
    public async Task WorkingDirectory_Should_Be_The_Pinned_Cli_Package_Whatever_Upstream_Configures_In_Its_Own_Checkout()
    {
        var fileSystem = Checkout();
        _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(RepositoryRoot, "external", "opencode", ".opencode"));

        var directory = new PinnedServerCommand(fileSystem).WorkingDirectory;

        await Assert.That(directory).IsEqualTo(fileSystem.Path.Combine(RepositoryRoot, "external", "opencode", "packages", "cli"));
    }

    [Test]
    [Arguments("opencode.json")]
    [Arguments("opencode.jsonc")]
    public async Task WorkingDirectory_Should_Refuse_Project_Configuration_Above_The_Checkout(string name)
    {
        var fileSystem = Checkout();
        var parent = fileSystem.Path.GetDirectoryName(RepositoryRoot) ?? throw new InvalidOperationException("The repository has no parent.");
        var configuration = fileSystem.Path.Combine(parent, name);
        _ = fileSystem.Initialize().With(new FileDescription(configuration, "{\"mcp\":{}}"));

        var refusal = await Assert.That(() => new PinnedServerCommand(fileSystem).WorkingDirectory).Throws<InvalidOperationException>();

        await Assert.That(refusal!.Message).Contains(configuration);
    }

    [Test]
    public async Task WorkingDirectory_Should_Refuse_A_Project_Directory_At_The_Repository_Root()
    {
        var fileSystem = Checkout();
        var configuration = fileSystem.Path.Combine(RepositoryRoot, ".opencode");
        _ = fileSystem.Directory.CreateDirectory(configuration);

        var refusal = await Assert.That(() => new PinnedServerCommand(fileSystem).WorkingDirectory).Throws<InvalidOperationException>();

        await Assert.That(refusal!.Message).Contains(configuration);
    }

    /// <summary>
    /// The walk from the CLI package leaves the upstream checkout through this repository's own
    /// <c>external</c> directory before it reaches the repository root.
    /// </summary>
    [Test]
    public async Task WorkingDirectory_Should_Refuse_A_Project_Directory_Beside_The_Upstream_Checkout()
    {
        var fileSystem = Checkout();
        var configuration = fileSystem.Path.Combine(RepositoryRoot, "external", ".opencode");
        _ = fileSystem.Directory.CreateDirectory(configuration);

        var refusal = await Assert.That(() => new PinnedServerCommand(fileSystem).WorkingDirectory).Throws<InvalidOperationException>();

        await Assert.That(refusal!.Message).Contains(configuration);
    }

    [Test]
    [Arguments("opencode.json")]
    [Arguments("opencode.jsonc")]
    public async Task WorkingDirectory_Should_Refuse_A_Config_File_Beside_The_Upstream_Checkout(string name)
    {
        var fileSystem = Checkout();
        var configuration = fileSystem.Path.Combine(RepositoryRoot, "external", name);
        _ = fileSystem.Initialize().With(new FileDescription(configuration, "{\"mcp\":{}}"));

        var refusal = await Assert.That(() => new PinnedServerCommand(fileSystem).WorkingDirectory).Throws<InvalidOperationException>();

        await Assert.That(refusal!.Message).Contains(configuration);
    }

    private static MockFileSystem Checkout()
    {
        var fileSystem = new MockFileSystem();
        _ = fileSystem.Directory.CreateDirectory(AppContext.BaseDirectory);
        _ = fileSystem.Initialize().With(new FileDescription(fileSystem.Path.Combine(RepositoryRoot, "OpenCode.slnx"), string.Empty));
        return fileSystem;
    }
}
