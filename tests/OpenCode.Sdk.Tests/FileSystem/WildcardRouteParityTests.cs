using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.FileSystem;

/// <summary>
/// Upstream's generated client fills <c>/api/fs/read/*</c> with <c>encodePath</c>: each
/// <c>/</c>-separated segment through <c>encodeURIComponent</c>. <c>tools/oracles/wildcard-path.ts</c>
/// calls that client over the corpus beside the expected file and records the request path it
/// sent; the SDK must put the same path on the wire. The path is read from the request the
/// handler received, so a target framework whose <c>Uri</c> rewrote an escape would fail here.
/// </summary>
public sealed class WildcardRouteParityTests
{
    [Test]
    public async Task ReadFileAsync_Should_Send_The_Path_Upstreams_Client_Sends()
    {
        using var verdicts = JsonDocument.Parse(new FixtureLoader().LoadJson("Routes.wildcard-path-expected.json"));
        var disagreements = new List<string>();

        foreach (var verdict in verdicts.RootElement.EnumerateArray())
        {
            var path = verdict.GetProperty("path").GetString()!;
            var expected = verdict.GetProperty("route").GetString();
            using var scenario = ContractScenario.Responding(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });

            _ = await scenario.Client.FileSystem.ReadFileAsync(new FsReadRequest { Path = path });

            var sent = scenario.Requests.Single().RequestUri!.AbsolutePath;
            if (!StringComparer.Ordinal.Equals(expected, sent))
            {
                disagreements.Add($"{JsonSerializer.Serialize(path)}: upstream {expected}, SDK {sent}");
            }
        }

        await Assert.That(verdicts.RootElement.GetArrayLength()).IsGreaterThan(0);
        await Assert.That(disagreements).IsEmpty();
    }
}
