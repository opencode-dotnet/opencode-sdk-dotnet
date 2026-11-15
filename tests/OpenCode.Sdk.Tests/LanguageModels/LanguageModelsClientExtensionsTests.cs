using System.Net;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.LanguageModels;

public sealed class LanguageModelsClientExtensionsTests
{
    private readonly FixtureLoader _fixtures = new();

    [Test]
    public async Task WaitForDefaultAsync_Should_Use_The_Pipeline_Clock()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var polls = 0;
        using var scenario = PipelineScenario.Responding(request =>
        {
            var body = WireBodyData.LocationEnvelope("null");
            if (request.RequestUri!.AbsolutePath == "/api/plugin")
            {
                body = ++polls == 1 ? WireBodyData.EmptyPluginList : _fixtures.LoadJson("LanguageModels.one-plugin-list.json");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }, networkTimeout: Timeout.InfiniteTimeSpan, time: time);
        var client = new LanguageModelsClient(scenario.Pipeline);

        var response = await new ManualClockWaits(time).ConsumeWaitsAsync(
            token => client.WaitForDefaultAsync(cancellationToken: token), [TimeSpan.FromMilliseconds(25)], caller);

        await Assert.That(response.Default).IsNull();
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin", "/api/plugin", "/api/model/default"], CollectionOrdering.Matching);
        await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(0);
    }
}
