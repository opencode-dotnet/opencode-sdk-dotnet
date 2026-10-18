using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class ConsumerPinGuardTests
{
    [Test]
    public async Task ConsumerPinGuard_Should_Keep_Consumer_Pins_Aligned_With_The_Snapshot()
    {
        var fileSystem = new RealFileSystem();
        var stale = await new ConsumerPinGuard(fileSystem).FindAsync(new RepositoryRoot(fileSystem).Locate());

        await Assert.That(stale).IsEmpty();
    }

    [Test]
    [Arguments("8.7.6")]
    [Arguments("9.8.7")]
    public async Task ConsumerPinGuard_Should_Report_Every_Stale_Site_In_Path_Then_Line_Order(string pin)
    {
        var scenario = new RepositoryGateScenario()
            .WithFile("spec/SNAPSHOT.md", $"| Upstream release tag | `v{pin}` |")
            .WithFile("docs/guide/nested/Z.md", "at the pin (`v4.0.1`)\nnpm install -g @opencode/cli@3.0.1")
            .WithFile("README.md", $"npm install -g @opencode/cli@{pin}\n\nnpm install -g @opencode/cli@2.0.1; npm install -g @opencode/cli@1.0.1")
            .WithFile("docs/guide/A.md", $"at the pin (`v{pin}`)\n\n\nat the pin (`v5.0.1`)")
            .WithFile(".github/ISSUE_TEMPLATE/nested/bug.yml", "id: package-version\nplaceholder: 7.0.1\nid: server-version\nplaceholder: 6.0.1\nid: os\nplaceholder: 8.0.1")
            .WithFile("docs/guide/ignored.txt", "npm install -g @opencode/cli@0.0.1")
            .WithFile("docs/adr/history.md", "npm install -g @opencode/cli@0.0.1");
        await scenario.BuildAsync();

        var stale = await new ConsumerPinGuard(scenario.FileSystem).FindAsync(scenario.Root);

        await Assert.That(stale).IsEquivalentTo(
            [$".github/ISSUE_TEMPLATE/nested/bug.yml:4: consumer pin 6.0.1; expected {pin}.",
             $"README.md:3: consumer pin 2.0.1; expected {pin}.",
             $"README.md:3: consumer pin 1.0.1; expected {pin}.",
             $"docs/guide/A.md:4: consumer pin 5.0.1; expected {pin}.",
             $"docs/guide/nested/Z.md:1: consumer pin 4.0.1; expected {pin}.",
             $"docs/guide/nested/Z.md:2: consumer pin 3.0.1; expected {pin}."], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Observed on `@opencode/cli@1.2.3`.")]
    [Arguments("Verified on `@opencode/cli@1.2.3`.")]
    [Arguments("confirmed at `v1.2.3`.")]
    [Arguments("a 1.2.3 service re-applies its saved env")]
    public async Task ConsumerPinGuard_Should_Preserve_Historical_Versions(string evidence)
    {
        var scenario = new RepositoryGateScenario()
            .WithFile("spec/SNAPSHOT.md", "| Upstream release tag | `v8.7.6` |")
            .WithFile("README.md", "npm install -g @opencode/cli@8.7.6")
            .WithFile("docs/guide/evidence.md", evidence)
            .WithFile(".github/ISSUE_TEMPLATE/bug.yml", "id: server-version\nplaceholder: 8.7.6");
        await scenario.BuildAsync();

        var stale = await new ConsumerPinGuard(scenario.FileSystem).FindAsync(scenario.Root);

        await Assert.That(stale).IsEmpty();
    }

    [Test]
    [Arguments("\"")]
    [Arguments("'")]
    public async Task ConsumerPinGuard_Should_Report_Quoted_Stale_Sites(string quote)
    {
        var scenario = new RepositoryGateScenario()
            .WithFile("spec/SNAPSHOT.md", "| Upstream release tag | `v8.7.6` |")
            .WithFile("README.md", $"npm install -g {quote}@opencode/cli@8.7.6{quote}\nnpm install -g {quote}@opencode/cli@2.0.1{quote}")
            .WithFile("docs/guide/evidence.md", $"Observed on `@opencode/cli@2.0.15`; {quote}@opencode/cli@1.2.3{quote}.")
            .WithFile(".github/ISSUE_TEMPLATE/bug.yml", $"id: {quote}server-version{quote}\nplaceholder: {quote}6.0.1{quote}\nplaceholder: 5.0.1\nid: {quote}package-version{quote}\nplaceholder: {quote}7.0.1{quote}");
        await scenario.BuildAsync();

        var stale = await new ConsumerPinGuard(scenario.FileSystem).FindAsync(scenario.Root);

        await Assert.That(stale).IsEquivalentTo(
            [".github/ISSUE_TEMPLATE/bug.yml:2: consumer pin 6.0.1; expected 8.7.6.",
             ".github/ISSUE_TEMPLATE/bug.yml:3: consumer pin 5.0.1; expected 8.7.6.",
             "README.md:2: consumer pin 2.0.1; expected 8.7.6."], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("", "spec/SNAPSHOT.md: expected exactly one '| Upstream release tag | `vX.Y.Z` |' row; found 0 matches.")]
    [Arguments("| Upstream release tag | v8.7.6 |", "spec/SNAPSHOT.md: expected exactly one '| Upstream release tag | `vX.Y.Z` |' row; found 0 matches.")]
    [Arguments("| Upstream release tag | `v8.7.6` |\n| Upstream release tag | `v9.8.7` |", "spec/SNAPSHOT.md: expected exactly one '| Upstream release tag | `vX.Y.Z` |' row; found 2 matches.")]
    public async Task ConsumerPinGuard_Should_Explain_Invalid_Snapshot_Rows(string snapshot, string expected)
    {
        var scenario = new RepositoryGateScenario().WithFile("spec/SNAPSHOT.md", snapshot);
        await scenario.BuildAsync();

        var stale = await new ConsumerPinGuard(scenario.FileSystem).FindAsync(scenario.Root);

        await Assert.That(stale).IsEquivalentTo([expected], CollectionOrdering.Matching);
    }
}
