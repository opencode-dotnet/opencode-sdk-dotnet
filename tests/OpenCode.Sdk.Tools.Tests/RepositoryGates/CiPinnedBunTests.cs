using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>
/// CI is the exact-pin authority for the Bun that runs the pinned server: it installs the version
/// the upstream checkout's <c>packageManager</c> names, and it never sets the switch that lets a
/// local gate host run on another Bun (<c>PinnedServerPrerequisites.BunMismatchVariable</c> in
/// <c>OpenCode.Sdk.Tests</c>, whose name these tests repeat because Tools.Tests cannot reference it).
/// </summary>
public sealed class CiPinnedBunTests
{
    private const string BunMismatchVariable = "OPENCODE_SDK_TESTS_BUN_MISMATCH";
    private const string PinnedBunInstall = "bun-version-file: external/opencode/package.json";

    private readonly RealFileSystem _fileSystem = new();

    [Test]
    public async Task Ci_Should_Never_Set_The_Bun_Mismatch_Switch()
    {
        var root = new RepositoryRoot(_fileSystem).Locate();
        var files = _fileSystem.Directory
            .EnumerateFiles(_fileSystem.Path.Combine(root, ".github"), "*", SearchOption.AllDirectories)
            .ToList();
        var contents = await Task.WhenAll(files.Select(file => _fileSystem.File.ReadAllTextAsync(file)));
        var mentioning = files
            .Where((_, index) => contents[index].Contains(BunMismatchVariable, StringComparison.Ordinal))
            .Select(file => _fileSystem.Path.GetRelativePath(root, file))
            .ToList();

        await Assert.That(files).IsNotEmpty();
        await Assert.That(mentioning).IsEmpty();
    }

    [Test]
    public async Task Ci_Should_Install_The_Bun_The_Pinned_Checkout_Names()
    {
        var root = new RepositoryRoot(_fileSystem).Locate();
        var action = await _fileSystem.File.ReadAllTextAsync(
            _fileSystem.Path.Combine(root, ".github", "actions", "setup-pinned-bun", "action.yml"));

        await Assert.That(action).Contains(PinnedBunInstall);
    }
}
