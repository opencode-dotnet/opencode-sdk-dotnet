using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The machine-wide configuration an owned server reads, which loads as managed settings above
/// every source the isolation sets. On Windows its directory follows <c>ProgramData</c>, and each
/// owned server takes an empty one inside its run root; on Linux and macOS no variable moves it,
/// and an owned launch refuses to start while one exists, which the isolation's unit tests prove.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SystemConfigurationIsolationLiveTests
{
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>
    /// A configuration document seeded under the run root's own <c>ProgramData</c> reaches the
    /// server as a loaded source, so the server took its system directory from the variable the
    /// isolation sets, not from the machine's <c>%ProgramData%\opencode</c>.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task GetConfigAsync_Should_Load_The_System_Directory_From_The_Servers_Own_ProgramData(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no variable moves the system directory; an owned launch refuses one that exists");
            return;
        }

        using var runRoot = new TestRunRoot(FileSystem);
        var seeded = await SeedSystemDocumentAsync(runRoot, cancellationToken);
        await using var server = await new PinnedServerLaunch(FileSystem).StartAsync(runRoot, cancellationToken: cancellationToken);
        using var workspace = new TestWorkspace(FileSystem, runRoot.Path);
        using var client = server.CreateClient(options => options.Location = new LocationSelector { Directory = workspace.Path });

        var response = await client.Config.GetConfigAsync(cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        var documents = response.Config.OfType<ConfigDocument>().Select(static document => document.Path).OfType<string>().ToList();
        Console.WriteLine("system-config-isolation: documents " + string.Join(", ", documents));
        await Assert.That(documents).Contains(path => string.Equals(path, seeded, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes an empty configuration document where the server's system directory resolves.</summary>
    private static async Task<string> SeedSystemDocumentAsync(TestRunRoot runRoot, CancellationToken cancellationToken)
    {
        var system = FileSystem.Path.Combine(SystemConfigurationSources.ProgramDataFor(FileSystem, runRoot.Path), "opencode");
        _ = FileSystem.Directory.CreateDirectory(system);
        var document = FileSystem.Path.Combine(system, "opencode.json");
#if NET
        await FileSystem.File.WriteAllTextAsync(document, "{}", cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
        FileSystem.File.WriteAllText(document, "{}");
        await Task.CompletedTask;
#endif
        return document;
    }
}
