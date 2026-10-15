using System.Runtime.InteropServices;
using System.Text;
using NSubstitute;
using OpenCode.Sdk.Internal;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService;

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

    /// <summary>
    /// The sidecar's readers hold it with delete sharing: the SDK's own reader, another client
    /// polling it, and a scanner such as real-time antivirus or the indexer, which opens every fresh
    /// file. The replace succeeds under them on every platform (on Windows through the
    /// POSIX-semantics rename, where the platform's <c>MoveFileEx</c> replace fails), and the reader
    /// keeps reading the contents it opened.
    /// </summary>
    [Test]
    public async Task Rename_Should_Replace_A_Destination_A_Delete_Sharing_Reader_Holds_And_Leave_The_Reader_The_Old_Contents()
    {
        using var directory = new OwnedTemporaryDirectory();
        var source = directory.File("temporary.json");
        var destination = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(source, "published");
        directory.FileSystem.File.WriteAllText(destination, "previous");

        using (var reader = new StreamReader(ServiceFileSystem.OpenRead(destination)))
        {
            new ServiceFileSystem().Rename(source, destination);

            await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("published");
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("previous");
        }

        await Assert.That(directory.FileSystem.File.Exists(source)).IsFalse();
    }

    /// <summary>
    /// A holder that shares no delete keeps the destination on Windows: the replace fails with the
    /// sharing violation the publication reports and retries, the destination keeps its contents,
    /// and the handle the replace opened on the source is released, so nothing else holds it.
    /// <c>rename(2)</c> replaces whatever holds the destination.
    /// </summary>
    [Test]
    public async Task Rename_Should_Leave_A_Destination_Held_Without_Delete_Sharing_In_Place_On_Windows()
    {
        using var directory = new OwnedTemporaryDirectory();
        var source = directory.File("temporary.json");
        var destination = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(source, "published");
        directory.FileSystem.File.WriteAllText(destination, "previous");

        using (directory.FileSystem.FileStream.New(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!OperatingSystem.IsWindows())
            {
                new ServiceFileSystem().Rename(source, destination);
                Console.WriteLine("branch: Unix (" + RuntimeInformation.OSDescription + ") — rename(2) replaces the destination whatever holds it");
                await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("published");
                return;
            }

            var failure = Assert.Throws<IOException>(() => new ServiceFileSystem().Rename(source, destination));
            Console.WriteLine("branch: Windows — " + failure.Message);
        }

        await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("previous");
        using (directory.FileSystem.FileStream.New(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.That(directory.FileSystem.File.Exists(source)).IsTrue();
        }
    }

    /// <summary>A volume without POSIX-semantics rename (FAT, some network shares) refuses the call with these errors.</summary>
    [Test]
    [Arguments(WindowsInterop.InvalidParameter)]
    [Arguments(WindowsInterop.NotSupported)]
    public async Task Rename_Should_Fall_Back_To_The_Platform_Replace_When_The_Volume_Refuses_The_Posix_Rename(int error)
    {
        using var directory = new OwnedTemporaryDirectory();
        var source = directory.File("temporary.json");
        var destination = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(source, "published");
        directory.FileSystem.File.WriteAllText(destination, "previous");
        var kernel = Substitute.For<IFileRenameKernel>();
        _ = kernel.Replace(source, destination).Returns(error);

        new ServiceFileSystem(kernel).Rename(source, destination);

        _ = kernel.Received(1).Replace(source, destination);
        await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("published");
        await Assert.That(directory.FileSystem.File.Exists(source)).IsFalse();
    }

    [Test]
    public async Task Rename_Should_Throw_Without_Falling_Back_When_The_Posix_Rename_Is_Refused_Otherwise()
    {
        using var directory = new OwnedTemporaryDirectory();
        var source = directory.File("temporary.json");
        var destination = directory.File("sidecar.json");
        directory.FileSystem.File.WriteAllText(source, "published");
        directory.FileSystem.File.WriteAllText(destination, "previous");
        var kernel = Substitute.For<IFileRenameKernel>();
        _ = kernel.Replace(source, destination).Returns(WindowsInterop.AccessDenied);

        _ = Assert.Throws<UnauthorizedAccessException>(() => new ServiceFileSystem(kernel).Rename(source, destination));

        await Assert.That(directory.FileSystem.File.ReadAllText(destination)).IsEqualTo("previous");
        await Assert.That(directory.FileSystem.File.ReadAllText(source)).IsEqualTo("published");
    }

    /// <summary>A failed POSIX-semantics rename throws what the platform's replace throws for the same error, so the publication's handling is unchanged.</summary>
    [Test]
    public async Task RenameFailure_Should_Map_Windows_Errors_To_The_Platform_Replace_Exceptions()
    {
        const int sharingViolation = 32;

        var missing = ServiceFileSystem.RenameFailure(WindowsInterop.FileNotFound, "a.tmp", "a.json");
        var denied = ServiceFileSystem.RenameFailure(WindowsInterop.AccessDenied, "a.tmp", "a.json");
        var shared = ServiceFileSystem.RenameFailure(sharingViolation, "a.tmp", "a.json");

        await Assert.That(missing).IsTypeOf<FileNotFoundException>();
        await Assert.That(((FileNotFoundException)missing).FileName).IsEqualTo("a.tmp");
        await Assert.That(denied).IsTypeOf<UnauthorizedAccessException>();
        await Assert.That(shared).IsTypeOf<IOException>();
        await Assert.That(shared.HResult).IsEqualTo(unchecked((int)0x80070020));
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
