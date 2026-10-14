#if NET
using PublicApiGenerator;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The core package's public surface against its reviewed baseline: a change to it is reviewed as
/// a diff of this file. It runs on .NET only: the net472 assembly renders without the experimental
/// marks, one of the reviewed differences <see cref="ShippedAssetPublicApiTests"/> holds every
/// shipped asset to.
/// </summary>
public sealed class PublicApiBaselineTests
{
    [Test]
    public async Task PublicApi_Should_Match_The_Reviewed_Baseline()
    {
        var api = typeof(OpenCodeClient).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            IncludeAssemblyAttributes = false,
            // The generator skips System and Microsoft namespaces by default; a public type there is
            // still part of the package's surface.
            AllowNamespacePrefixes = ["System", "Microsoft"],
        });

        var settings = new VerifySettings();
        settings.UseFileName("PublicApi");
        // Keep the reviewed baseline a normal LF-terminated text artifact on every OS.
        await Verify(string.Concat(api, "\n"), settings);
    }
}
#endif
