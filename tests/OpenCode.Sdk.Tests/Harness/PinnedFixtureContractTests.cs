using System.Globalization;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The contract between this repository's JavaScript fixtures and the accepted snapshot. A fixture
/// that boots the pinned server imports upstream modules by name, and upstream is free to rename a
/// package under a refresh: it renamed its whole workspace scope once already. Nothing else catches
/// that, because the refresh receipt watches the sources the hand-written doors read rather than the
/// module identifiers a fixture writes, so the mismatch would otherwise reach the suite as a server
/// that exits before readiness.
/// </summary>
/// <remarks>
/// This reads the snapshot on disk rather than an installed tree on purpose. A developer machine
/// that installed before a rename keeps the previous revision's workspace links alongside the new
/// ones, so the old names still resolve there and a local run agrees while hosted CI does not.
/// </remarks>
public sealed class PinnedFixtureContractTests
{
    private static readonly RealFileSystem FileSystem = new();

    [Test]
    public async Task Fixtures_Should_Name_Only_Packages_The_Pinned_Snapshot_Declares(
        CancellationToken cancellationToken)
    {
        var loader = new FixtureLoader();
        var inventory = await PinnedPackageInventory.LoadAsync(FileSystem, cancellationToken);
        var fixtures = loader.Names(".js");

        var undeclared = new List<string>();
        var referenced = 0;
        foreach (var fixture in fixtures)
        {
            foreach (var package in UpstreamPackageReferences.In(loader.LoadText(fixture)))
            {
                referenced++;
                if (!inventory.Declares(package))
                {
                    undeclared.Add($"{fixture} names '{package}'");
                }
            }
        }

        await Assert.That(undeclared).IsEmpty();
        Console.WriteLine(
            "pinned-fixture-contract: " +
            referenced.ToString(CultureInfo.InvariantCulture) + " upstream package reference(s) across " +
            fixtures.Count.ToString(CultureInfo.InvariantCulture) + " fixture(s)");
    }
}
