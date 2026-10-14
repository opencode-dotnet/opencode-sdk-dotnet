#if NET
using Microsoft.Extensions.DependencyInjection;
using PublicApiGenerator;

namespace OpenCode.Sdk.Extensions.Tests;

/// <summary>
/// The Extensions package's public surface against its reviewed baseline, as the core package's is
/// against its own: a change to it is reviewed as a diff of this file. It runs on .NET only: the
/// net472 assembly renders without the trimming annotations, one of the reviewed differences
/// <see cref="ShippedAssetPublicApiTests"/> holds every shipped asset to.
/// </summary>
public sealed class PublicApiBaselineTests
{
    [Test]
    public async Task PublicApi_Should_Match_The_Reviewed_Baseline()
    {
        var api = typeof(OpenCodeServiceCollectionExtensions).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            IncludeAssemblyAttributes = false,
            // The generator skips System and Microsoft namespaces by default; the package's entry
            // point lives in Microsoft.Extensions.DependencyInjection by convention.
            AllowNamespacePrefixes = ["System", "Microsoft"],
        });

        var settings = new VerifySettings();
        settings.UseFileName("PublicApi");

        // Keep the reviewed baseline a normal LF-terminated text artifact on every OS.
        await Verify(string.Concat(api, "\n"), settings);
    }
}
#endif
