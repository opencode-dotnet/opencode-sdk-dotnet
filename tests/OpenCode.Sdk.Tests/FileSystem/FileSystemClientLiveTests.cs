using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class FileSystemClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string ChildDirectory = "sdk-live-child";
    private const string ChildFile = "sdk-live-child/sdk-live-find-target.txt";
    private const string RootFile = "sdk-live-root-file.txt";
    private const string WriteTarget = "sdk-live-written/nested/blob.bin";
    private const string ReadTarget = "sdk live read/über notes #1.txt";

    [Test]
    [Timeout(60_000)]
    public async Task FindEntriesAsync_And_ListEntriesAsync_Should_Report_The_Workspace_Entries(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        var owner = Guid.NewGuid().ToString("N");
        _ = workspace.WriteTextFile(RootFile, owner);
        _ = workspace.WriteTextFile(ChildFile, "find target");
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });

        var found = await client.FileSystem.FindEntriesAsync(
            new FsFindRequest
            {
                Query = "sdk-live-find-target",
                Type = FsFindRequestType.File,
                Limit = "10",
            },
            cancellationToken: cancellationToken);

        await Assert.That(found.Status).IsEqualTo(200);
        await Assert.That(found.IsError).IsFalse();
        var foundFile = found.Entries.Single(item => NormalizeSeparators(item.Path) == ChildFile);
        await Assert.That(foundFile.Type).IsEqualTo(FileSystemEntryType.File);

        var listed = await client.FileSystem.ListEntriesAsync(
            new FsListRequest { Path = "." }, cancellationToken: cancellationToken);

        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        await Assert.That(listed.Location.Directory).IsEqualTo(found.Location.Directory);
        await Assert.That(workspace.HasTextFile(listed.Location.Directory, RootFile, owner)).IsTrue();
        var rootFile = listed.Entries.Single(item => NormalizeSeparators(item.Path) == RootFile);
        await Assert.That(rootFile.Type).IsEqualTo(FileSystemEntryType.File);
        var childDirectory = listed.Entries.Single(
            item => NormalizeSeparators(item.Path) == ChildDirectory + "/");
        await Assert.That(childDirectory.Type).IsEqualTo(FileSystemEntryType.Directory);

        Console.WriteLine(
            "filesystem-live: find-status=" + Number(found.Status) +
            " find-path=" + foundFile.Path +
            " list-status=" + Number(listed.Status) +
            " directory=" + childDirectory.Path);
    }

    /// <summary>
    /// The route carries the nested path escaped segment by segment, the server confines it to the
    /// location and answers the file's bytes under a MIME type taken from its extension.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task ReadFileAsync_Should_Return_The_File_Bytes_And_Its_Mime_Type(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        var text = "sdk live read " + Guid.NewGuid().ToString("N") + " ünïcødé";
        _ = workspace.WriteTextFile(ReadTarget, text);
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });

        var read = await client.FileSystem.ReadFileAsync(new FsReadRequest { Path = ReadTarget }, cancellationToken: cancellationToken);

        await Assert.That(read.Status).IsEqualTo(200);
        await Assert.That(read.Content.ToArray()).IsEquivalentTo(Encoding.UTF8.GetBytes(text));
        await Assert.That(MediaTypeHeaderValue.Parse(read.ContentType!).MediaType).IsEqualTo("text/plain");

        var missing = await client.FileSystem.ReadFileAsync(
            new FsReadRequest { Path = "sdk-live-missing.txt" },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        await Assert.That(missing.Status).IsEqualTo(404);
        await Assert.That(missing.Error).IsTypeOf<FileNotFoundError>();

        Console.WriteLine(
            "filesystem-live: read-status=" + Number(read.Status) +
            " bytes=" + Number(read.Content.Length) +
            " content-type=" + read.ContentType +
            " missing-status=" + Number(missing.Status));
    }

    /// <summary>
    /// The write takes the caller's stream as the body and answers the resolved absolute path; the
    /// read of the same relative path returns the same bytes, and the file on disk holds them. The
    /// target is relative to an owned workspace, because a write is not confined to the location.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task WriteFileAsync_Should_Write_Bytes_That_ReadFileAsync_Reads_Back(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        byte[] bytes = [.. Encoding.UTF8.GetBytes("sdk live write " + Guid.NewGuid().ToString("N")), 0x00, 0xFF, 0xFE];
        using var source = new MemoryStream(bytes);

        var written = await client.Experimental.WriteFileAsync(
            new ExperimentalFsWriteRequest { Path = WriteTarget },
            source,
            cancellationToken: cancellationToken);

        await Assert.That(written.Status).IsEqualTo(200);
        await Assert.That(NormalizeSeparators(written.File.Path)).EndsWith(WriteTarget);
        await Assert.That(source.Position).IsEqualTo(bytes.Length);

        var read = await client.FileSystem.ReadFileAsync(new FsReadRequest { Path = WriteTarget }, cancellationToken: cancellationToken);

        await Assert.That(read.Content.ToArray()).IsEquivalentTo(bytes);
        await Assert.That(workspace.HasBytes(WriteTarget, bytes)).IsTrue();

        Console.WriteLine(
            "filesystem-live: write-status=" + Number(written.Status) +
            " bytes=" + Number(bytes.Length) +
            " read-back=" + Number(read.Content.Length));
    }

    /// <summary>
    /// A path that leaves the location is refused by the server before any byte is read, with the
    /// declared 404 and its typed <see cref="FileNotFoundError"/>.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task ReadFileAsync_Should_Not_Read_A_File_Outside_The_Location(CancellationToken cancellationToken)
    {
        using var inside = server.CreateWorkspace();
        using var outside = server.CreateWorkspace();
        var secret = outside.WriteTextFile(RootFile, "outside " + Guid.NewGuid().ToString("N"));
        using var client = server.CreateClient(new LocationSelector { Directory = inside.Path });

        var refused = await client.FileSystem.ReadFileAsync(
            new FsReadRequest { Path = secret.Replace('\\', '/') },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(refused.Status).IsEqualTo(404);
        await Assert.That(refused.Error).IsTypeOf<FileNotFoundError>();
    }

    private static string NormalizeSeparators(string value) => value.Replace('\\', '/');

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
