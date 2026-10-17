using System.Globalization;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Harness;

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
        await AssertFileAbsentAsync(fileSystem, outside);
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
        await AssertFileAbsentAsync(fileSystem, outside);
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
        await Assert.That(expected).Satisfies(fileSystem.File.Exists);
        using var reader = fileSystem.File.OpenText(expected);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("owned content");
    }

    private static async Task AssertFileAbsentAsync(MockFileSystem fileSystem, string file) =>
        await Assert.That(file).Satisfies(path => !fileSystem.File.Exists(path));

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

    [Test]
    public async Task ReadBytes_Should_Return_The_Actual_Nested_File_Contents()
    {
        var fileSystem = new MockFileSystem();
        using var workspace = new TestWorkspace(fileSystem, fileSystem.Path.GetTempPath());
        _ = workspace.WriteTextFile("nested/bytes.txt", "cab");

        await Assert.That(workspace.ReadBytes("nested/bytes.txt"))
            .IsEquivalentTo(new byte[] { 99, 97, 98 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadTextFile_Should_Return_The_Actual_Content_From_A_Reported_Directory()
    {
        var fileSystem = new MockFileSystem();
        using var workspace = new TestWorkspace(fileSystem, fileSystem.Path.GetTempPath());
        var reported = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "reported-project");
        _ = fileSystem.Initialize().With(new FileDescription(
            fileSystem.Path.Combine(reported, "nested", "owner.txt"), "another-owner"));

        await Assert.That(workspace.ReadTextFile(reported, "nested/owner.txt")).IsEqualTo("another-owner");
    }

    [Test]
    public async Task Reads_Should_Return_The_Wrong_Content_Rather_Than_An_Ownership_Verdict()
    {
        var fileSystem = new MockFileSystem();
        using var workspace = new TestWorkspace(fileSystem, fileSystem.Path.GetTempPath());
        _ = workspace.WriteTextFile("marker.txt", "owned");
        _ = workspace.WriteTextFile("marker.txt", "wrong");

        await Assert.That(workspace.ReadTextFile(workspace.Path, "marker.txt")).IsEqualTo("wrong");
        await Assert.That(workspace.ReadBytes("marker.txt"))
            .IsEquivalentTo(new byte[] { 119, 114, 111, 110, 103 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reads_Should_Propagate_A_Missing_File()
    {
        var fileSystem = new MockFileSystem();
        using var workspace = new TestWorkspace(fileSystem, fileSystem.Path.GetTempPath());

        _ = await Assert.That(() => workspace.ReadBytes("missing.txt")).ThrowsExactly<FileNotFoundException>();
        _ = await Assert.That(() => workspace.ReadTextFile(workspace.Path, "missing.txt")).ThrowsExactly<FileNotFoundException>();
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task ReadTextFile_Should_Reject_A_Blank_Directory(string directory)
    {
        var fileSystem = new MockFileSystem();
        using var workspace = new TestWorkspace(fileSystem, fileSystem.Path.GetTempPath());

        var exception = await Assert.That(() => workspace.ReadTextFile(directory, "marker.txt")).ThrowsExactly<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("directory");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("/rooted-outside.txt")]
    [Arguments("../traversal-outside.txt")]
    [Arguments("../{0}-sibling/outside.txt")]
    public async Task Reads_Should_Reject_A_Blank_Or_Escaping_Path(string pathTemplate)
    {
        var fileSystem = new MockFileSystem();
        using var workspace = new TestWorkspace(fileSystem, fileSystem.Path.GetTempPath());
        var relativePath = string.Format(CultureInfo.InvariantCulture, pathTemplate, fileSystem.Path.GetFileName(workspace.Path));
        _ = fileSystem.Initialize().With(
            new FileDescription(fileSystem.Path.GetFullPath("/rooted-outside.txt"), "rooted content"),
            new FileDescription(fileSystem.Path.GetFullPath(fileSystem.Path.Combine(workspace.Path, "../traversal-outside.txt")), "traversal content"),
            new FileDescription(workspace.Path + "-sibling/outside.txt", "sibling content"));

        var bytes = await Assert.That(() => workspace.ReadBytes(relativePath)).ThrowsExactly<ArgumentException>();
        await Assert.That(bytes!.ParamName).IsEqualTo("relativePath");
        var text = await Assert.That(() => workspace.ReadTextFile(workspace.Path, relativePath)).ThrowsExactly<ArgumentException>();
        await Assert.That(text!.ParamName).IsEqualTo("relativePath");
    }
}
