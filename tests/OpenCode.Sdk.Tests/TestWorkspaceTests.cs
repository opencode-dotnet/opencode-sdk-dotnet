using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests;

public sealed class TestWorkspaceTests
{
    [Test]
    public async Task WriteTextFile_Should_Reject_A_Rooted_Path_Without_Writing_Outside_The_Workspace()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "test-workspace-rooted");
        using var workspace = new TestWorkspace(fileSystem, runRoot);
        var outside = fileSystem.Path.GetFullPath(
            fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "test-workspace-rooted-outside.txt"));

        var exception = await Assert
            .That(() => workspace.WriteTextFile(outside, "outside"))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("relativePath");
        await Assert.That(fileSystem.File.Exists(outside)).IsFalse();
    }

    [Test]
    public async Task WriteTextFile_Should_Reject_Traversal_Without_Writing_Outside_The_Workspace()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "test-workspace-traversal");
        using var workspace = new TestWorkspace(fileSystem, runRoot);
        var outside = fileSystem.Path.GetFullPath(
            fileSystem.Path.Combine(workspace.Path, "..", "test-workspace-traversal-outside.txt"));

        var exception = await Assert
            .That(() => workspace.WriteTextFile("../test-workspace-traversal-outside.txt", "outside"))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("relativePath");
        await Assert.That(fileSystem.File.Exists(outside)).IsFalse();
    }

    [Test]
    public async Task WriteTextFile_Should_Write_A_Valid_Nested_Path()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "test-workspace-valid");
        using var workspace = new TestWorkspace(fileSystem, runRoot);

        var written = workspace.WriteTextFile("nested/owned.txt", "owned content");

        var expected = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(workspace.Path, "nested", "owned.txt"));
        await Assert.That(written).IsEqualTo(expected);
        await Assert.That(fileSystem.File.Exists(expected)).IsTrue();
        using var reader = fileSystem.File.OpenText(expected);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("owned content");
    }

    [Test]
    public async Task HasTextFile_Should_Distinguish_The_Owned_Marker_From_Wrong_Or_Missing_Content()
    {
        var fileSystem = new MockFileSystem();
        var runRoot = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "test-workspace-marker");
        using var workspace = new TestWorkspace(fileSystem, runRoot);
        const string marker = "owned-marker.txt";
        const string owner = "independently-seeded-owner";
        _ = workspace.WriteTextFile(marker, owner);

        await Assert.That(workspace.HasTextFile(workspace.Path, marker, owner)).IsTrue();
        await Assert.That(workspace.HasTextFile(workspace.Path, marker, "another-owner")).IsFalse();
        await Assert.That(workspace.HasTextFile(workspace.Path, "missing-marker.txt", owner)).IsFalse();
    }
}
