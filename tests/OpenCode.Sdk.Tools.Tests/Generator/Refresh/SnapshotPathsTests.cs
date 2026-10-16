using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Generator.Refresh;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SnapshotPathsTests
{
    [Test]
    public async Task RepositoryDefault_Should_Use_The_Accepted_Document_Path()
    {
        await Assert.That(GenerationRequest.RepositoryDefault(verify: true).SpecPath).IsEqualTo(SnapshotPaths.AcceptedDocument);
    }
}
