using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ReferencesClientLiveTests(SimulatedDriveServerFixture server)
{
    /// <summary>The git reference the upstream checkout's own configuration declares.</summary>
    private const string UpstreamGitReference = "effect";

    /// <summary>The local reference the upstream checkout's own configuration declares.</summary>
    private const string UpstreamLocalReference = "opencode-local";

    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Timeout(60_000)]
    public async Task ListReferencesAsync_Should_Report_The_Typed_Local_Reference(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var response = await LiveReadiness.WaitAsync(
            token => client.References.ListReferencesAsync(cancellationToken: token),
            result => result.References.Any(item => item.Name == SimulationConfigSeed.ReferenceName),
            "seeded references", cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Location.Directory).IsEqualTo(workspace.Path);
        var reference = response.References.Single(item => item.Name == SimulationConfigSeed.ReferenceName);
        await Assert.That(reference.Path).IsEqualTo(workspace.Path);
        await Assert.That(reference.Description).IsEqualTo(SimulationConfigSeed.ReferenceDescription);
        await Assert.That(reference.Source).IsTypeOf<ReferenceLocalSource>();
        var local = reference.Source as ReferenceLocalSource;
        await Assert.That(local?.Path).IsEqualTo(workspace.Path);

        Console.WriteLine(
            "references-live: status=" + Number(response.Status) + " name=" + reference.Name +
            " path=" + reference.Path);
    }

    /// <summary>
    /// At the server's own working directory, inside the upstream checkout, project configuration
    /// discovery would add the checkout's own references: a GitHub repository the server clones and
    /// a local one. One pass over the loaded configuration adds every reference, the seeded one
    /// among them, so once the seeded one is listed the checkout's would be too. The proof runs here
    /// because this fixture seeds that reference: the absence needs it as a barrier, references
    /// load after the server answers, and the default fixture's configuration is empty. The pinned
    /// CLI's own reading of the switch is proven by the configuration sources it reports.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task ListReferencesAsync_Should_Not_Carry_The_Upstream_Checkout_References_At_The_Server_Working_Directory(
        CancellationToken cancellationToken)
    {
        var workingDirectory = new PinnedServerCommand(FileSystem).WorkingDirectory;
        using var client = server.CreateClient(new LocationSelector { Directory = workingDirectory });
        var response = await LiveReadiness.WaitAsync(
            token => client.References.ListReferencesAsync(cancellationToken: token),
            result => result.References.Any(item => item.Name == SimulationConfigSeed.ReferenceName),
            "seeded references", cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Location.Directory).IsEqualTo(workingDirectory);
        var names = response.References.Select(static reference => reference.Name).ToList();
        Console.WriteLine("references-live: at " + workingDirectory + ": " + string.Join(", ", names));
        await Assert.That(names).DoesNotContain(UpstreamGitReference);
        await Assert.That(names).DoesNotContain(UpstreamLocalReference);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
