using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Output;

public sealed class PlacementValidatorTests
{
    [Test]
    [Arguments("RetiredClient.cs", true)]
    [Arguments("Retired/RetiredClient.cs", true)]
    [Arguments("Retired/Models/RetiredModel.cs", true)]
    [Arguments("Retired/Internal/RetiredAdapter.cs", true)]
    [Arguments("Retired/Models/Messages/RetiredMessage.cs", true)]
    [Arguments("Common/Errors/RetiredError.cs", true)]
    [Arguments("Internal/Serialization/RetiredContext.cs", true)]
    [Arguments("Retired/Other/RetiredClient.cs", false)]
    [Arguments("Retired/Internal/Deep/RetiredAdapter.cs", false)]
    [Arguments("Retired/Models/A/B/RetiredModel.cs", false)]
    [Arguments("Retired/Models/RetiredModel.txt", false)]
    [Arguments("Internal/Transport/RetiredTransport.cs", false)]
    [Arguments("Retired/models/RetiredModel.cs", false)]
    [Arguments("Retired/RetiredClient.CS", false)]
    public async Task ValidateManifestSourcePath_Should_Admit_Only_The_Owned_Shapes(string path, bool admitted)
    {
        var outcome = path;
        try
        {
            PlacementValidator.ValidateManifestSourcePath(path);
        }
        catch (OutputRefusalException refusal)
        {
            outcome = refusal.Message;
        }

        await Assert.That(outcome).IsEqualTo(admitted ? path
            : $"Generation manifest path '{path}' is outside the owned source directories.");
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
