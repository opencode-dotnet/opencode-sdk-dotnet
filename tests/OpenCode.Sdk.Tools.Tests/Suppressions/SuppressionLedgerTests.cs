using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// A suppression silences a gate, so every one is counted against the committed ledger, and the
/// ledger moves in the same pull request as the suppression it records. Beside the count stand the
/// shape rules that keep a suppression visible: one declaration per section glob, no section naming
/// a file that is gone, and one channel per rule per file. The ledger counts suppressions; it does
/// not judge their reasons, which review and the maintainer's approval do.
/// </summary>
public sealed class SuppressionLedgerTests
{
    [ClassDataSource<SuppressionInventorySession>(Shared = SharedType.PerTestSession)]
    public required SuppressionInventorySession Session { get; init; }

    [Test]
    public async Task Ledger_Should_Count_Every_Suppression_In_The_Repository()
    {
        var inventory = Session.Inventory;

        await Verify(SuppressionLedger.Render(inventory.Suppressions));
    }

    [Test]
    public async Task EditorConfig_Should_Declare_Each_Section_Once()
    {
        var inventory = Session.Inventory;

        await Assert.That(AnalyzerConfigAudit.FindDuplicateSections(inventory.AnalyzerConfigs)).IsEmpty();
    }

    [Test]
    public async Task EditorConfig_Should_Name_Only_Existing_Files()
    {
        var inventory = Session.Inventory;

        await Assert.That(AnalyzerConfigAudit.FindUnmatchedSections(inventory)).IsEmpty();
    }

    [Test]
    public async Task Rule_Should_Be_Suppressed_Through_One_Channel_Per_File()
    {
        var inventory = Session.Inventory;

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEmpty();
    }

}
