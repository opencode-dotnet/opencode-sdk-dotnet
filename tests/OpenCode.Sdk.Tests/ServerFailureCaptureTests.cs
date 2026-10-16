using System.Globalization;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// What a failure keeps of the server's own log folder: its log files, the newest part of an
/// oversized one, and nothing else from the data root around them.
/// </summary>
public sealed class ServerFailureCaptureTests
{
    private const int KeptBytes = 1024 * 1024;

    [Test]
    public async Task CaptureAsync_Should_Keep_The_Server_Log_Files_And_Nothing_Else_From_Its_Data_Root()
    {
        var fileSystem = new MockFileSystem();
        var artifacts = new ServerFailureArtifacts(fileSystem, fileSystem.Path.GetFullPath("results"));
        var data = fileSystem.Path.GetFullPath(fileSystem.Path.Combine("run", "data"));
        var logs = fileSystem.Path.Combine(data, "opencode", "log");
        _ = fileSystem.Initialize().With(
            new DirectoryDescription(fileSystem.Path.Combine(logs, "opencode-local.log.trim")),
            new FileDescription(fileSystem.Path.Combine(logs, "opencode-local.log"), "spawning process git rev-parse"),
            new FileDescription(fileSystem.Path.Combine(logs, "notes.txt"), "not a log"),
            new FileDescription(fileSystem.Path.Combine(data, "opencode.db"), "stored credentials"));

        var failed = await new ServerFailureCapture(artifacts, fileSystem, new OwnedOperationDeadline())
            .CaptureAsync(output: null, processId: null, external: false, failure: null, [], logs);

        var kept = fileSystem.Path.Combine(artifacts.Directory, "server-logs");
        await Assert.That(failed).IsNull();
        await Assert.That(fileSystem.Directory.GetFileSystemEntries(kept).Select(fileSystem.Path.GetFileName).OfType<string>())
            .IsEquivalentTo(["opencode-local.log"]);
        await Assert.That(await ReadAsync(fileSystem, fileSystem.Path.Combine(kept, "opencode-local.log")))
            .IsEqualTo("spawning process git rev-parse");
        await Assert.That(fileSystem.Directory.EnumerateFiles(artifacts.Directory, "opencode.db", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task CaptureAsync_Should_Keep_The_Newest_Bytes_Of_An_Oversized_Server_Log()
    {
        var fileSystem = new MockFileSystem();
        var artifacts = new ServerFailureArtifacts(fileSystem, fileSystem.Path.GetFullPath("results"));
        var logs = fileSystem.Path.GetFullPath(fileSystem.Path.Combine("run", "data", "opencode", "log"));
        const string newest = "location services booted durationMs=61234";
        _ = fileSystem.Initialize().With(
            new FileDescription(fileSystem.Path.Combine(logs, "opencode.log"), "oldest line\n" + new string('x', KeptBytes) + newest));

        _ = await new ServerFailureCapture(artifacts, fileSystem, new OwnedOperationDeadline())
            .CaptureAsync(output: null, processId: null, external: false, failure: null, [], logs);

        var text = await ReadAsync(fileSystem, fileSystem.Path.Combine(artifacts.Directory, "server-logs", "opencode.log"));
        await Assert.That(text).StartsWith("[truncated:");
        await Assert.That(text).EndsWith(newest);
        await Assert.That(text).DoesNotContain("oldest line");
    }

    [Test]
    public async Task CaptureAsync_Should_Keep_The_Eight_Newest_Server_Logs()
    {
        var fileSystem = new MockFileSystem();
        var artifacts = new ServerFailureArtifacts(fileSystem, fileSystem.Path.GetFullPath("results"));
        var logs = fileSystem.Path.GetFullPath(fileSystem.Path.Combine("run", "data", "opencode", "log"));
        var written = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < 10; index++)
        {
            var log = fileSystem.Path.Combine(logs, LogName(index));
            _ = fileSystem.Initialize().With(new FileDescription(log, "line " + index.ToString(CultureInfo.InvariantCulture)));
            fileSystem.File.SetLastWriteTimeUtc(log, written.AddMinutes(index));
        }

        _ = await new ServerFailureCapture(artifacts, fileSystem, new OwnedOperationDeadline())
            .CaptureAsync(output: null, processId: null, external: false, failure: null, [], logs);

        await Assert.That(fileSystem.Directory.GetFiles(fileSystem.Path.Combine(artifacts.Directory, "server-logs"))
                .Select(fileSystem.Path.GetFileName).OfType<string>())
            .IsEquivalentTo(Enumerable.Range(2, 8).Select(LogName));
    }

    [Test]
    public async Task CaptureAsync_Should_Keep_Nothing_When_The_Server_Wrote_No_Log_Folder()
    {
        var fileSystem = new MockFileSystem();
        var artifacts = new ServerFailureArtifacts(fileSystem, fileSystem.Path.GetFullPath("results"));

        var failed = await new ServerFailureCapture(artifacts, fileSystem, new OwnedOperationDeadline())
            .CaptureAsync(output: null, processId: null, external: false, failure: null, [],
                fileSystem.Path.GetFullPath(fileSystem.Path.Combine("run", "data", "opencode", "log")));

        await Assert.That(failed).IsNull();
        await Assert.That(fileSystem.Directory.Exists(fileSystem.Path.Combine(artifacts.Directory, "server-logs"))).IsFalse();
    }

    private static string LogName(int index) => "opencode-" + index.ToString(CultureInfo.InvariantCulture) + ".log";

    private static async Task<string> ReadAsync(MockFileSystem fileSystem, string path)
    {
        using var reader = fileSystem.File.OpenText(path);
        return await reader.ReadToEndAsync();
    }
}
