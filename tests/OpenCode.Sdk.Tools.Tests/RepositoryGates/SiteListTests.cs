using System.Globalization;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class SiteListTests
{
    private const string List = "RepositoryGates/sites.txt";
    private const string File = "tests/P/A.cs";

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task SiteList_Should_Report_Growth_With_All_Current_Candidates(int allowed)
    {
        IReadOnlyList<SiteListEntry> entries = allowed == 0 ? [] : [new(File, allowed, 1)];
        RepositoryGateSite[] sites = [new(File, 12, 0, "Rule A", "rule text"), new(File, 28, 1, "Rule A", "rule text")];

        var messages = SiteList.Compare(List, entries, sites, "Rule A");

        await Assert.That(messages).IsEquivalentTo(
            [string.Create(CultureInfo.InvariantCulture, $"{List}:1: Rule A: growth in {File}: listed {allowed}, found 2.\nCurrent candidate sites (the count baseline cannot identify which was added):\n{File}:12: Rule A: rule text\n{File}:28: Rule A: rule text\nDo not grow the list without the named PR reason; fix the added site.")], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1, "replace 'tests/P/A.cs\t2' with 'tests/P/A.cs\t1'.")]
    [Arguments(0, "delete 'tests/P/A.cs\t2' (no remaining sites or file disappeared).")]
    public async Task SiteList_Should_Request_A_List_Shrink(int remaining, string instruction)
    {
        IReadOnlyList<RepositoryGateSite> sites = remaining == 0 ? [] : [new(File, 12, 0, "Rule A", "rule text")];

        var messages = SiteList.Compare(List, [new(File, 2, 3)], sites, "Rule A");

        await Assert.That(messages).IsEquivalentTo(
            [string.Create(CultureInfo.InvariantCulture, $"{List}:3: Rule A: found {remaining}, listed 2; shrink the list: {instruction}")], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SiteList_Should_Not_Offset_One_Files_Growth_With_Anothers_Shrink()
    {
        var messages = SiteList.Compare(List, [new(File, 1, 1)], [new("tests/P/B.cs", 9, 0, "Rule A", "rule text")], "Rule A");

        await Assert.That(messages).Count().IsEqualTo(2);
        await Assert.That(messages[0]).Contains("shrink the list: delete");
        await Assert.That(messages[1]).Contains("tests/P/B.cs:9: Rule A: rule text");
    }

    [Test]
    public async Task SiteList_Should_Ignore_Line_Shifts_When_Counts_Match()
    {
        await Assert.That(SiteList.Compare(List, [new(File, 1, 1)], [new(File, 900, 0, "Rule A", "rule text")], "Rule A")).IsEmpty();
    }

    [Test]
    public async Task SiteList_Should_Parse_Positive_Counts_With_Row_Numbers()
    {
        await Assert.That(SiteList.Parse(List, $"{File}\t2\n\ntests/P/B.cs\t1\n"))
            .IsEquivalentTo([new SiteListEntry(File, 2, 1), new SiteListEntry("tests/P/B.cs", 1, 3)], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("tests/P/A.cs 1")]
    [Arguments("tests/P/A.cs\t0")]
    [Arguments("tests/P/A.cs\t-1")]
    [Arguments("tests/P/A.cs\t1\textra")]
    [Arguments("tests/../A.cs\t1")]
    [Arguments("/tests/P/A.cs\t1")]
    [Arguments("tests\\P\\A.cs\t1")]
    [Arguments("src/A.cs\t1")]
    [Arguments("tests/A.txt\t1")]
    [Arguments("tests/A\0.cs\t1")]
    [Arguments("tests/P/A.cs\t2147483648")]
    [Arguments("tests/P/A.cs\ttext")]
    [Arguments("tests/P/A.cs\t1\ntests/P/A.cs\t1")]
    [Arguments("tests/P/B.cs\t1\ntests/P/A.cs\t1")]
    public async Task SiteList_Should_Reject_Malformed_Or_Unsorted_Rows(string rows)
    {
        var exception = await Assert.That(() => SiteList.Parse(List, rows)).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).StartsWith(List + ":");
    }
}
