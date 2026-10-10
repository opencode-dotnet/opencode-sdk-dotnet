using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// What an owned server reads from outside its run root. A request at the server's own working
/// directory, the CLI package inside the upstream checkout, is where every request without a
/// location resolves; with project configuration discovery on, the server would load the
/// checkout's own <c>.opencode</c> - its skills, plugins, tools and a git reference it clones from
/// GitHub - and every configuration above the checkout, the developer's own included. The server's
/// temp root would be the developer's own. The server reports both, so these read them back.
/// </summary>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ProjectConfigurationIsolationLiveTests(PinnedOpenCodeServerFixture server)
{
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>
    /// The location's configuration answers with every source it loaded, the isolated global
    /// config root among them, so the listing is never empty for a server that read nothing. A
    /// reference comes only from a loaded configuration document, so a checkout document absent
    /// here is a git reference the server does not clone.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task GetConfigAsync_Should_Load_Nothing_From_Above_The_Server_Working_Directory(CancellationToken cancellationToken)
    {
        RequireOwned();
        var workingDirectory = new PinnedServerCommand(FileSystem).WorkingDirectory;
        using var client = server.CreateClient(new LocationSelector { Directory = workingDirectory });

        var response = await client.Config.GetConfigAsync(cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        var sources = response.Config.Select(Source).OfType<string>().ToList();
        Console.WriteLine("project-config-isolation: config sources at " + workingDirectory + ": " + string.Join(", ", sources));
        await Assert.That(sources.Any(InsideRunRoot)).IsTrue();
        await Assert.That(sources.Where(source => !InsideRunRoot(source))).IsEmpty();
    }

    /// <summary>
    /// The server's temp root, which agents may use as an approved external directory, is the
    /// runtime's temp directory plus the server's own name; under the run root it is the server's
    /// alone. The server creates it at start, and reports where it is.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task GetInfoAsync_Should_Report_A_Temp_Root_Inside_The_Run_Root(CancellationToken cancellationToken)
    {
        RequireOwned();
        using var client = server.CreateClient();

        var response = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        var reported = response.ServerInfo.Paths.Tmp;
        Console.WriteLine("project-config-isolation: temp root " + reported);
        var separator = FileSystem.Path.DirectorySeparatorChar;
        await Assert.That(reported.TrimEnd(separator))
            .EndsWith(separator + RunRootName + separator + "tmp" + separator + "opencode");
        await Assert.That(FileSystem.Directory.Exists(FileSystem.Path.Combine(server.RunRoot.Path, "tmp", "opencode"))).IsTrue();
    }

    /// <summary>
    /// The run root's own directory name, unique to this fixture. A path the server names is
    /// compared by it rather than by the whole run root, because the server resolves links in the
    /// roots it reports (macOS's temp directory is reached through one).
    /// </summary>
    private string RunRootName => FileSystem.Path.GetFileName(server.RunRoot.Path);

    private static string? Source(IConfigEntry entry) => entry switch
    {
        ConfigDocument document => document.Path,
        ConfigDirectory directory => directory.Path,
        _ => null,
    };

    private bool InsideRunRoot(string path)
    {
        var separator = FileSystem.Path.DirectorySeparatorChar;
        return path.Contains(separator + RunRootName + separator, StringComparison.Ordinal);
    }

    private void RequireOwned()
    {
        if (server.IsExternal)
        {
            throw new InvalidOperationException(
                "The project configuration isolation proof requires the owned pinned server; "
                + "an external endpoint's configuration is its operator's.");
        }
    }
}
