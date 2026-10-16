using System.Globalization;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Extensions.Tests;

/// <summary>
/// Every asset the Extensions package ships exposes the reviewed public API baseline: the netstandard2.0
/// compatibility asset, the .NET Framework asset and each modern one, each read from the build
/// output, so a target framework that compiled a different surface fails here rather than reaching a
/// consumer on that framework. The reviewed differences between assets are
/// <see cref="ShippedAssetSurface.ExpectedAsync"/>'s.
/// </summary>
public sealed class ShippedAssetPublicApiTests
{
    /// <summary>The shipped assets' target frameworks, as the build recorded them.</summary>
    /// <returns>One case per asset.</returns>
    public static IEnumerable<string> ShippedTargetFrameworks() => ShippedAssetSurface.TargetFrameworks();

    [Test]
    public async Task The_Build_Should_Record_The_Compatibility_Asset_Among_The_Shipped_Ones()
    {
        await Assert.That(ShippedAssetSurface.TargetFrameworks()).Contains("netstandard2.0");
    }

    [Test]
    [MethodDataSource(nameof(ShippedTargetFrameworks))]
    public async Task Shipped_Asset_Should_Expose_The_Reviewed_Public_Api(string targetFramework)
    {
        var expected = await ShippedAssetSurface.ExpectedAsync(targetFramework);

        var rendered = await ShippedAssetSurface.RenderAsync(targetFramework);

        await Assert.That(FirstDifference(rendered, expected)).IsNull()
            .Because("the " + targetFramework + " asset's public API must match Snapshots/PublicApi.verified.txt");
    }

    /// <summary>The first line where the two texts differ, with both versions, or null when they are equal.</summary>
    private static string? FirstDifference(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return null;
        }

        var actualLines = actual.Split('\n');
        var expectedLines = expected.Split('\n');
        var line = 0;
        while (line < actualLines.Length && line < expectedLines.Length && actualLines[line] == expectedLines[line])
        {
            line++;
        }

        var actualLine = line < actualLines.Length ? actualLines[line] : "(end)";
        var expectedLine = line < expectedLines.Length ? expectedLines[line] : "(end)";
        return "line " + (line + 1).ToString(CultureInfo.InvariantCulture) + ": asset has '" + actualLine + "', baseline has '" + expectedLine + "'";
    }
}
