using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Output;

public sealed class PlacementValidatorTests
{
    [Test]
    [Arguments("RetiredClient.cs")]
    [Arguments("Retired/RetiredClient.cs")]
    [Arguments("Retired/Models/RetiredModel.cs")]
    [Arguments("Retired/Internal/RetiredAdapter.cs")]
    [Arguments("Retired/Models/Messages/RetiredMessage.cs")]
    public async Task ValidateManifestSourcePath_Should_Keep_Retired_Family_Shapes(string path)
    {
        var admitted = path;
        try
        {
            PlacementValidator.ValidateManifestSourcePath(path);
        }
        catch (OutputRefusalException refusal)
        {
            admitted = refusal.Message;
        }

        await Assert.That(admitted).IsEqualTo(path);
    }

    [Test]
    [Arguments("../Escape.cs")]
    [Arguments("/Escape.cs")]
    [Arguments("A//B.cs")]
    [Arguments("A/./B.cs")]
    [Arguments("A\\B.cs")]
    public async Task ResolveOwnedPath_Should_Refuse_Unsafe_Paths_Without_Mutation(string path)
    {
        var fs = GenerationTestData.CreateFileSystem();
        var placement = new PlacementValidator(fs);

        var refusal = await Assert.That(() => placement.ResolveOwnedPath(GenerationTestData.OutputRoot, path))
            .Throws<OutputRefusalException>();

        await Assert.That(refusal!.Message).IsEqualTo($"Owned path '{path}' is unsafe.");
        await Assert.That(fs.Directory.EnumerateFiles(GenerationTestData.OutputRoot)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ValidateSources_Should_Reject_Duplicate_Emitted_Paths_As_An_Invariant()
    {
        var fs = GenerationTestData.CreateFileSystem();
        var placement = new PlacementValidator(fs);
        var sources = new[] { GenerationTestData.Source("A.cs", "first"), GenerationTestData.Source("a.cs", "second") };

        var failure = await Assert.That(() => placement.ValidateSources(GenerationTestData.OutputRoot, sources, [], []))
            .Throws<InvalidOperationException>();

        await Assert.That(failure!.Message).IsEqualTo("Generated source path 'a.cs' is duplicated.");
        await Assert.That(fs.Directory.EnumerateFiles(GenerationTestData.OutputRoot)).Count().IsEqualTo(1);
    }
}
