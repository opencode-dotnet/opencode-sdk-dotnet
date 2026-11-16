using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class CorsOriginPolicyTests
{
    [Test]
    public async Task IsOrigin_Should_Match_Every_Upstream_Cors_Verdict()
    {
        using var verdicts = JsonDocument.Parse(new FixtureLoader().LoadJson("BackgroundService.cors-origin-expected.json"));
        var disagreements = new List<string>();
        foreach (var row in verdicts.RootElement.EnumerateArray())
        {
            var origin = row.GetProperty("origin").GetString()!;
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
}
