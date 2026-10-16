using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class TestRunRootLocationTests
{
    [Test]
    public async Task Resolve_Should_Keep_Run_Roots_Out_Of_The_User_Profile_And_Any_Repository()
    {
        var fileSystem = new MockFileSystem();

        var resolved = new TestRunRootLocation(fileSystem, configured: null).Resolve();

        // The temp root is inside the profile on Windows only, where a workspace beneath it would
        // walk up through the developer's own home; elsewhere it already has a clean chain. The
        // machine-wide application data root is the standard user-writable place outside both.
        var expected = OperatingSystem.IsWindows()
            ? fileSystem.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "opencode-sdk-tests")
            : fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "opencode-sdk-tests");
        await Assert.That(resolved).IsEqualTo(expected);
    }

    [Test]
    public async Task Resolve_Should_Prefer_The_Configured_Directory()
    {
        var fileSystem = new MockFileSystem();
        var configured = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "elsewhere");

        var resolved = new TestRunRootLocation(fileSystem, configured).Resolve();

        await Assert.That(resolved).IsEqualTo(configured);
    }

    [Test]
    public async Task Resolve_Should_Refuse_A_Directory_Beneath_Developer_State()
    {
        var fileSystem = new MockFileSystem();
        var parent = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "projects");
        var state = fileSystem.Path.Combine(parent, ".opencode");
        _ = fileSystem.Directory.CreateDirectory(state);

        var refusal = Assert.Throws<InvalidOperationException>(
            () => _ = new TestRunRootLocation(fileSystem, fileSystem.Path.Combine(parent, "runs")).Resolve());

        await Assert.That(refusal.Message).Contains(state);
        await Assert.That(refusal.Message).Contains(TestRunRootLocation.OverrideVariable);
    }
}
