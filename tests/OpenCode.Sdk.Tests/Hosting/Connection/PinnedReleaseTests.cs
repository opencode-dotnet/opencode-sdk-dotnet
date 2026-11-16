using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class PinnedReleaseTests
{
    [Test]
    public async Task Version_Should_Equal_The_Receipt_Release_Tag_Without_Its_Prefix()
    {
        using var receipt = JsonDocument.Parse(new FixtureLoader().LoadJson("Pinned.receipt.json"));
        var releaseTag = receipt.RootElement.GetProperty("releaseTag").GetString();

        await Assert.That(releaseTag).IsNotNull();
        await Assert.That(releaseTag).StartsWith("v");
        await Assert.That(PinnedRelease.Version).IsEqualTo(releaseTag[1..]);
    }
}
