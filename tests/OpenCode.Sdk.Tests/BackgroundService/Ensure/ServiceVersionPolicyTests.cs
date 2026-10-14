using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.BackgroundService.Ensure;

/// <summary>
/// The pinned client's <c>matchesVersion</c>, and the CLI's mismatch flag as the entry snapshot
/// resolves it: Ignore strips the expected version from the election, Replace and Error keep it.
/// </summary>
public sealed class ServiceVersionPolicyTests
{
    [Test]
    public async Task MatchesVersion_Should_Return_True_When_Nothing_Is_Expected()
    {
        await Assert.That(ServiceVersionPolicy.MatchesVersion("2.0.3", expected: null)).IsTrue();
        await Assert.That(ServiceVersionPolicy.MatchesVersion(reported: null, expected: null)).IsTrue();
    }

    [Test]
    public async Task MatchesVersion_Should_Return_False_When_The_Reported_Version_Is_Missing()
    {
        await Assert.That(ServiceVersionPolicy.MatchesVersion(reported: null, "2.0.3")).IsFalse();
    }

    [Test]
    public async Task MatchesVersion_Should_Return_True_When_The_Reported_Version_Equals_The_Expected()
    {
        await Assert.That(ServiceVersionPolicy.MatchesVersion("2.0.3", "2.0.3")).IsTrue();
    }

    [Test]
    public async Task MatchesVersion_Should_Return_False_When_The_Reported_Version_Differs()
    {
        await Assert.That(ServiceVersionPolicy.MatchesVersion("2.0.3", "2.0.2")).IsFalse();
    }

    [Test]
    public async Task LoopVersion_Should_Be_Null_When_The_Policy_Is_Ignore()
    {
        var request = EnsureRequest.Snapshot(new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.3", VersionPolicy = OpenCodeServerVersionPolicy.Ignore });

        await Assert.That(request.LoopVersion).IsNull();
    }

    [Test]
    [Arguments(OpenCodeServerVersionPolicy.Replace)]
    [Arguments(OpenCodeServerVersionPolicy.Error)]
    public async Task LoopVersion_Should_Keep_The_Expected_Version_Under_Replace_And_Error(OpenCodeServerVersionPolicy policy)
    {
        var request = EnsureRequest.Snapshot(new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.3", VersionPolicy = policy });

        await Assert.That(request.LoopVersion).IsEqualTo("2.0.3");
    }
}
