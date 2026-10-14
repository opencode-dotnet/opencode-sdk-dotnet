using System.Text;
using OpenCode.Sdk.Internal;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.BackgroundService;

/// <summary>
/// The shipped <see cref="ServiceFileSystem"/> against the real filesystem, inside one owned
/// temporary directory: exclusive creation, the existing-target refusal that keeps a live
/// registration intact, and exact reads. Level 3 by design; the level-1/2 double is the
/// <c>TestablyServiceFileSystem</c> adapter.
/// </summary>
public sealed class ServiceFileSystemTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("{\"url\":\"http://127.0.0.1:1\"}");

    [Test]
    public async Task TryCreateExclusiveAsync_Should_Create_A_Missing_File_With_Its_Bytes()
    {
        using var directory = new OwnedTemporaryDirectory();
        var path = directory.File("created.json");

        var created = await new ServiceFileSystem().TryCreateExclusiveAsync(path, Content, CancellationToken.None);

        await Assert.That(created).IsTrue();
        await Assert.That(directory.FileSystem.File.ReadAllBytes(path)).IsEquivalentTo(Content);
    }

    [Test]
    public async Task TryCreateExclusiveAsync_Should_Return_False_For_An_Existing_File_And_Keep_Its_Content()
    {
        using var directory = new OwnedTemporaryDirectory();
        var path = directory.File("existing.json");
        directory.FileSystem.File.WriteAllText(path, "live");

        var created = await new ServiceFileSystem().TryCreateExclusiveAsync(path, Content, CancellationToken.None);

        await Assert.That(created).IsFalse();
        await Assert.That(directory.FileSystem.File.ReadAllText(path)).IsEqualTo("live");
    }

    [Test]
    public async Task TryCreateExclusiveAsync_Should_Throw_For_A_Missing_Directory_And_Create_Nothing()
    {
        using var directory = new OwnedTemporaryDirectory();
        var path = directory.File("missing", "nested.json");

        _ = await Assert
            .That(async () => _ = await new ServiceFileSystem().TryCreateExclusiveAsync(path, Content, CancellationToken.None))
            .Throws<DirectoryNotFoundException>();

        await Assert.That(directory.FileSystem.Directory.Exists(directory.File("missing"))).IsFalse();
    }

    [Test]
    public async Task TryReadAllBytesAsync_Should_Read_Exact_Bytes()
    {
        using var directory = new OwnedTemporaryDirectory();
        var path = directory.File("read.json");
        directory.FileSystem.File.WriteAllBytes(path, Content);

        var bytes = await new ServiceFileSystem().TryReadAllBytesAsync(path, CancellationToken.None);

        await Assert.That(bytes).IsEquivalentTo(Content);
    }

    [Test]
    public async Task TryReadAllBytesAsync_Should_Return_Null_For_A_Missing_File_Or_Directory()
    {
        using var directory = new OwnedTemporaryDirectory();
        var fileSystem = new ServiceFileSystem();

        await Assert.That(await fileSystem.TryReadAllBytesAsync(directory.File("absent.json"), CancellationToken.None)).IsNull();
        await Assert.That(await fileSystem.TryReadAllBytesAsync(directory.File("missing", "nested.json"), CancellationToken.None)).IsNull();
    }

    /// <summary>
    /// The daemon removes its registration on exit while clients poll it every 25 ms. libuv opens
    /// every file with delete sharing, so on Windows the removal never fails because a reader holds
    /// the file (measured against Bun: <c>unlink</c> is <c>EBUSY</c> under read-only sharing and
    /// succeeds under delete sharing). A rename over an open file fails on Windows whatever the
    /// reader shares, upstream's readers included, so only the removal is asserted. Unix never
    /// blocks either, so the assertion is Windows' and holds everywhere.
    /// </summary>
    [Test]
    public async Task OpenRead_Should_Not_Block_A_Delete_Of_The_File()
    {
        using var directory = new OwnedTemporaryDirectory();
        var path = directory.File("service.json");
        directory.FileSystem.File.WriteAllBytes(path, Content);

        using (ServiceFileSystem.OpenRead(path))
        {
            directory.FileSystem.File.Delete(path);
        }

        await Assert.That(directory.FileSystem.File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task TryDelete_Should_Remove_An_Existing_File()
    {
        using var directory = new OwnedTemporaryDirectory();
        var path = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(path, "x");

        var removed = new ServiceFileSystem().TryDelete(path);

        await Assert.That(removed).IsTrue();
        await Assert.That(directory.FileSystem.File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task TryDelete_Should_Return_False_For_A_Missing_File()
    {
        using var directory = new OwnedTemporaryDirectory();

        var removed = new ServiceFileSystem().TryDelete(directory.File("absent.json"));

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task TryDelete_Should_Return_False_For_A_Missing_Directory()
    {
        using var directory = new OwnedTemporaryDirectory();

        var removed = new ServiceFileSystem().TryDelete(directory.File("missing", "nested.json"));

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task Rename_Should_Move_A_File_To_The_Destination()
    {
        using var directory = new OwnedTemporaryDirectory();
        var source = directory.File("temporary.json");
        var destination = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(source, "published");

        new ServiceFileSystem().Rename(source, destination);

        await Assert.That(directory.FileSystem.File.Exists(source)).IsFalse();
        await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("published");
    }

    [Test]
    public async Task Rename_Should_Replace_An_Existing_Destination()
    {
        using var directory = new OwnedTemporaryDirectory();
        var source = directory.File("temporary.json");
        var destination = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(source, "published");
        directory.FileSystem.File.WriteAllText(destination, "previous");

        new ServiceFileSystem().Rename(source, destination);

        await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("published");
    }

    [Test]
    public async Task Rename_Should_Leave_The_Destination_In_Place_When_The_Source_Is_Missing()
    {
        // A replace that fails must not have destroyed what it was replacing: a reader between the
        // two steps of a delete-then-move would see no sidecar at all.
        using var directory = new OwnedTemporaryDirectory();
        var destination = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(destination, "previous");

        _ = Assert.Throws<FileNotFoundException>(() =>
            new ServiceFileSystem().Rename(directory.File("absent.json"), destination));

        await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("previous");
    }

    [Test]
    public void Rename_Should_Throw_For_A_Missing_Source()
    {
        using var directory = new OwnedTemporaryDirectory();

        _ = Assert.Throws<FileNotFoundException>(() =>
            new ServiceFileSystem().Rename(directory.File("absent.json"), directory.File("sidecar.json")));
    }

#if NET
    [Test]
    public async Task TryCreateExclusiveAsync_Should_Create_With_User_Only_Access_On_Unix()
    {
        using var directory = new OwnedTemporaryDirectory();
        var path = directory.File("secret.json");

        _ = await new ServiceFileSystem().TryCreateExclusiveAsync(path, Content, CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            // Windows has no Unix mode; the file inherits the parent directory's DACL, which is
            // the documented behavior, and the assertion is that creation itself succeeded.
            await Assert.That(directory.FileSystem.File.Exists(path)).IsTrue();
            return;
        }

        await Assert.That(directory.FileSystem.File.GetUnixFileMode(path))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
#endif

    /// <summary>One per-test directory under the temp root, removed with everything it holds.</summary>
    private sealed class OwnedTemporaryDirectory : IDisposable
    {
        public OwnedTemporaryDirectory()
        {
            Path = FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "opencode-sdk-service-fs-" + Guid.NewGuid().ToString("N"));
            _ = FileSystem.Directory.CreateDirectory(Path);
        }

        public RealFileSystem FileSystem { get; } = new();

        public string Path { get; }

        public string File(params string[] segments) => FileSystem.Path.Combine([Path, .. segments]);

        public void Dispose() => FileSystem.Directory.Delete(Path, recursive: true);
    }
}
