using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class ServerFailureArtifactsTests
{
    [Test]
    public async Task WriteMetadataAsync_Should_Bound_Details_Without_Losing_Primary_Identity()
    {
        var fileSystem = new MockFileSystem();
        var artifacts = new ServerFailureArtifacts(fileSystem, fileSystem.Path.GetFullPath("results"));
        var path = artifacts.Mark(new InvalidOperationException("primary remains visible"), "bounded test", new string('x', 50_000));

        await artifacts.WriteMetadataAsync("owned", "42", []);

        using var reader = fileSystem.File.OpenText(path);
        var text = await reader.ReadToEndAsync();
        await Assert.That(text.Length).IsLessThanOrEqualTo(16_384);
        await Assert.That(text).Contains("[truncated]");
        await Assert.That(text).Contains("primary remains visible");
    }

    [Test]
    public async Task Mark_Should_Deduplicate_Only_The_Same_Invocation_And_Exception()
    {
        var fileSystem = new MockFileSystem();
        var artifacts = new ServerFailureArtifacts(fileSystem, fileSystem.Path.GetFullPath("results"));
        var failure = new InvalidOperationException("shared failure");
        var first = artifacts.Mark(failure, "first test", "phase=create", "first");
        var duplicate = artifacts.Mark(failure, "first test", "phase=create", "first");
        var second = artifacts.Mark(failure, "second test", "phase=connect", "second");

        await artifacts.WriteMetadataAsync("owned", "42", []);

        await Assert.That(first).IsEqualTo(duplicate);
        await Assert.That(second).IsNotEqualTo(first);
        await Assert.That(fileSystem.File.Exists(first)).IsTrue();
        await Assert.That(fileSystem.File.Exists(second)).IsTrue();
        await Assert.That(artifacts.Report(failure, "first")).IsTrue();
        await Assert.That(artifacts.Report(failure, "first")).IsFalse();
        await Assert.That(artifacts.Report(failure, "second")).IsTrue();
    }
}
