using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Vcs;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class VcsClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task GetVcsAsync_ListBranchesAsync_And_GetBaseAsync_Should_Report_The_Initialized_Main_Branch(
        CancellationToken cancellationToken)
    {
        using var repository = await server.CreateGitRepositoryWorkspaceAsync(cancellationToken);
        var location = repository.Location;
        using var client = server.CreateClient();
        var vcs = await LiveReadiness.GitAsync(client, location, cancellationToken);
        await Assert.That(vcs.Status).IsEqualTo(200);
        await Assert.That(vcs.IsError).IsFalse();
        await Assert.That(repository.OwnsDirectory(vcs.Location.Directory)).IsTrue();
        await Assert.That(vcs.Vcs.Branch.Current).IsEqualTo("main");
        await Assert.That(vcs.Vcs.Branch.Default).IsEqualTo("main");

        var branches = await client.Vcs.ListBranchesAsync(
            new VcsBranchListRequest { Location = location, Search = "mai", Limit = "1" },
            cancellationToken: cancellationToken);
        await Assert.That(branches.Status).IsEqualTo(200);
        await Assert.That(branches.IsError).IsFalse();
        await Assert.That(branches.Location.Directory).IsEqualTo(vcs.Location.Directory);
        await Assert.That(branches.Branches).IsEquivalentTo(["main"]);

        var reviewBase = await client.Vcs.GetBaseAsync(
            new VcsBaseRequest { Location = location }, cancellationToken: cancellationToken);
        await Assert.That(reviewBase.Status).IsEqualTo(200);
        await Assert.That(reviewBase.IsError).IsFalse();
        await Assert.That(reviewBase.Location.Directory).IsEqualTo(vcs.Location.Directory);
        await Assert.That(reviewBase.Base).IsNotNull();
        await Assert.That(reviewBase.Base!.Name).IsEqualTo("main");
        await Assert.That(reviewBase.Base.Ref).IsEqualTo("refs/heads/main");
        await Assert.That(reviewBase.Base.Source).IsEqualTo(VcsBaseSource.Default);

        Console.WriteLine(
            "vcs-live: operation=vcs.get+branches+base arm=success mode=metadata status=" +
            Number(vcs.Status) + "/" + Number(branches.Status) + "/" + Number(reviewBase.Status));
    }

    [Test]
    [Timeout(60_000)]
    public async Task GetStatusAsync_And_GetDiffAsync_Should_Report_A_Modified_Tracked_File(
        CancellationToken cancellationToken)
    {
        using var repository = await server.CreateGitRepositoryWorkspaceAsync(cancellationToken);
        var location = repository.Location;
        using var client = server.CreateClient();
        _ = await LiveReadiness.GitAsync(client, location, cancellationToken);
        repository.WriteModifiedTrackedFile();

        var status = await client.Vcs.GetStatusAsync(
            new VcsStatusRequest { Location = location }, cancellationToken: cancellationToken);
        await Assert.That(status.Status).IsEqualTo(200);
        await Assert.That(status.IsError).IsFalse();
        await Assert.That(repository.OwnsDirectory(status.Location.Directory)).IsTrue();
        var change = status.Changes.Single();
        await Assert.That(change.File).IsEqualTo("tracked.txt");
        await Assert.That(change.Status).IsEqualTo(VcsFileStatusStatus.Modified);
        await Assert.That(change.Additions).IsEqualTo(1);
        await Assert.That(change.Deletions).IsEqualTo(1);

        var diff = await client.Vcs.GetDiffAsync(
            new VcsDiffRequest { Location = location, Mode = VcsMode.Branch, Base = "main", Context = "0" },
            cancellationToken: cancellationToken);
        await Assert.That(diff.Status).IsEqualTo(200);
        await Assert.That(diff.IsError).IsFalse();
        await Assert.That(diff.Location.Directory).IsEqualTo(status.Location.Directory);
        var file = diff.Diffs.Single();
        await Assert.That(file.File).IsEqualTo("tracked.txt");
        await Assert.That(file.Status).IsEqualTo(FileDiffInfoStatus.Modified);
        await Assert.That(file.Additions).IsEqualTo(1);
        await Assert.That(file.Deletions).IsEqualTo(1);
        await Assert.That(string.IsNullOrWhiteSpace(file.Patch)).IsFalse();
        await Assert.That(file.Patch).Contains("-before");
        await Assert.That(file.Patch).Contains("+after");

        Console.WriteLine(
            "vcs-live: operation=vcs.status+diff arm=success mode=branch base=main context=0 status=" +
            Number(status.Status) + "/" + Number(diff.Status));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
