using System.Globalization;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Projects;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ProjectsClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string OwnerFile = "sdk-live-project-owner.txt";

    [Test]
    [Timeout(60_000)]
    public async Task GetLocationAsync_And_ListProjectsAsync_Should_Report_The_Workspace_Project(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        var owner = Guid.NewGuid().ToString("N");
        _ = workspace.WriteTextFile(OwnerFile, owner);
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });

        var current = await client.GetLocationAsync(
            cancellationToken: cancellationToken);

        await Assert.That(current.Status).IsEqualTo(200);
        await Assert.That(current.IsError).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace(current.ResolvedLocation.Project.Id)).IsFalse();
        await Assert.That(current.ResolvedLocation.Project.Canonical).IsEqualTo(current.ResolvedLocation.Project.Directory);
        await Assert.That(workspace.HasTextFile(current.ResolvedLocation.Project.Directory, OwnerFile, owner)).IsTrue();

        var listed = await client.Projects.ListProjectsAsync(cancellationToken: cancellationToken);

        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        var project = listed.Projects.Single(item => item.Id == current.ResolvedLocation.Project.Id);
        await Assert.That(project.Canonical).IsEqualTo(current.ResolvedLocation.Project.Canonical);
        await Assert.That(workspace.HasTextFile(project.Canonical, OwnerFile, owner)).IsTrue();

        Console.WriteLine(
            "projects-live: current=" + Number(current.Status) +
            " list=" + Number(listed.Status) +
            " id=" + project.Id +
            " directory=" + current.ResolvedLocation.Project.Directory);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
