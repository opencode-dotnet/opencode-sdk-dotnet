using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.CommentResidue;

/// <summary>
/// The repository's comments state their rules where they stand: no plan, design-document,
/// documentation-path or research-log pointers, no snapshot auto-accept, and no unexplained Stryker
/// suppression. The gate starts from an empty baseline, so any hit fails.
/// </summary>
public sealed class CommentResidueTests
{
    private readonly RealFileSystem _fileSystem = new();

    [Test]
    public async Task Repository_Should_Carry_No_Comment_Residue()
    {
        var hits = await new CommentResidueScan(_fileSystem)
            .ReadAsync(new RepositoryRoot(_fileSystem).Locate(), CancellationToken.None);

        await Assert.That(hits.Select(static hit => hit.ToString())).IsEmpty();
    }
}
