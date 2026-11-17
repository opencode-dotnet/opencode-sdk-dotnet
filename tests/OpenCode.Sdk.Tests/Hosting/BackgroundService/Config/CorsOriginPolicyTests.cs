using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class CorsOriginPolicyTests
{
    /// <summary>The SDK refuses ACE labels over 63 characters, while upstream accepts them.</summary>
    private static readonly string[] ExpectedDifferences =
    [
        "http://xn--9caaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "http://xn--9caaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    ];

    [Test]
    public async Task IsOrigin_Should_Match_Every_Upstream_Cors_Verdict()
    {
        using var verdicts = JsonDocument.Parse(new FixtureLoader().LoadJson("BackgroundService.cors-origin-expected.json"));
        var disagreements = new List<string>();
        foreach (var row in verdicts.RootElement.EnumerateArray())
        {
            var origin = row.GetProperty("origin").GetString()!;
            if (ExpectedDifferences.Contains(origin, StringComparer.Ordinal))
            {
                continue;
            }

            var expected = row.GetProperty("valid").GetBoolean();
            var actual = CorsOriginPolicy.IsOrigin(CorsOriginPolicy.TrimItem(origin));
            if (actual != expected)
            {
                disagreements.Add($"{row.GetProperty("origin").GetRawText()}: upstream {expected}, SDK {actual}");
            }
        }

        await Assert.That(verdicts.RootElement.GetArrayLength()).IsGreaterThanOrEqualTo(60);
        await Assert.That(disagreements).IsEmpty();
    }

    [Test]
    public async Task IsOrigin_Should_Preserve_Recorded_Upstream_Differences()
    {
        using var verdicts = JsonDocument.Parse(new FixtureLoader().LoadJson("BackgroundService.cors-origin-expected.json"));
        var upstream = verdicts.RootElement.EnumerateArray().ToDictionary(
            row => row.GetProperty("origin").GetString()!,
            row => row.GetProperty("valid").GetBoolean(),
            StringComparer.Ordinal);
        foreach (var origin in ExpectedDifferences)
        {
            await Assert.That(upstream.Keys).Contains(origin);
            await Assert.That(upstream[origin]).IsTrue().Because(origin);
            await Assert.That(CorsOriginPolicy.IsOrigin(CorsOriginPolicy.TrimItem(origin))).IsFalse().Because(origin);
        }
    }
}
