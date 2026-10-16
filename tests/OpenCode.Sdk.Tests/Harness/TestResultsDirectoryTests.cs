using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class TestResultsDirectoryTests
{
    [Test]
    public async Task Resolve_Should_Use_The_Forwarded_Results_Argument()
    {
        var fileSystem = new MockFileSystem();
        var resolver = new TestResultsDirectory(fileSystem);

        var path = resolver.Resolve(["test-host", "--results-directory", "retained-results"]);

        await Assert.That(path).IsEqualTo(fileSystem.Path.GetFullPath("retained-results"));
        await Assert.That(resolver.Resolve(["--results-directory=retained-results"])).IsEqualTo(path);
    }

    [Test]
    public async Task Resolve_Should_Use_Test_Results_When_The_Runner_Has_No_Override()
    {
        var fileSystem = new MockFileSystem();
        var resolver = new TestResultsDirectory(fileSystem);

        await Assert.That(resolver.Resolve([])).IsEqualTo(fileSystem.Path.GetFullPath("test-results"));
        _ = await Assert.That(() => resolver.Resolve(["--results-directory"])).Throws<ArgumentException>();
    }
}
