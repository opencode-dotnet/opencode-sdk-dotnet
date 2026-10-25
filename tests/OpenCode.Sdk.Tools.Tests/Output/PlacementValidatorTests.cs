using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Output;

public sealed class PlacementValidatorTests
{
    [Test]
    [Arguments("RetiredClient.cs", true, DisplayName = "root file")]
    [Arguments("Retired/RetiredClient.cs", true, DisplayName = "family root")]
    [Arguments("Models/RetiredModel.cs", true, DisplayName = "shape, not family membership: a reserved root at two segments is admitted")]
    [Arguments("Retired/Models/RetiredModel.cs", true, DisplayName = "family Models")]
    [Arguments("Retired/models/RetiredModel.cs", false, DisplayName = "family Models, case variant")]
    [Arguments("Retired/Internal/RetiredAdapter.cs", true, DisplayName = "family Internal")]
    [Arguments("Retired/internal/RetiredAdapter.cs", false, DisplayName = "family Internal, case variant")]
    [Arguments("Retired/Other/RetiredClient.cs", false, DisplayName = "family, other folder")]
    [Arguments("Internal/Serialization/RetiredContext.cs", true, DisplayName = "shared home Internal/Serialization")]
    [Arguments("internal/Serialization/RetiredContext.cs", false, DisplayName = "shared home Internal/Serialization, case variant")]
    [Arguments("Common/Errors/RetiredError.cs", true, DisplayName = "shared home Common/Errors")]
    [Arguments("Common/errors/RetiredError.cs", false, DisplayName = "shared home Common/Errors, case variant")]
    [Arguments("Common/Internal/RetiredHelper.cs", true, DisplayName = "shared home Common/Internal, admitted by both the Internal folder arm and the shared-home arm")]
    [Arguments("Common/internal/RetiredHelper.cs", false, DisplayName = "shared home Common/Internal, case variant")]
    [Arguments("Internal/Transport/RetiredTransport.cs", false, DisplayName = "shared root Internal, not a home")]
    [Arguments("Common/Other/RetiredHelper.cs", false, DisplayName = "shared root Common, not a home")]
    [Arguments("Retired/Models/Messages/RetiredMessage.cs", true, DisplayName = "model area")]
    [Arguments("Retired/models/Messages/RetiredMessage.cs", false, DisplayName = "model area, case variant")]
    [Arguments("Retired/Internal/Deep/RetiredAdapter.cs", false, DisplayName = "four segments under Internal")]
    [Arguments("Internal/Serialization/Deep/RetiredContext.cs", false, DisplayName = "four segments under a shared home")]
    [Arguments("Retired/Models/A/B/RetiredModel.cs", false, DisplayName = "five segments")]
    [Arguments("Retired/RetiredClient.CS", false, DisplayName = "suffix, case variant")]
    [Arguments("Retired/Models/RetiredModel.txt", false, DisplayName = "suffix, not C#")]
    [Arguments("Retired/Notes.cs.ics", false, DisplayName = "suffix, C# inside another extension")]
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
