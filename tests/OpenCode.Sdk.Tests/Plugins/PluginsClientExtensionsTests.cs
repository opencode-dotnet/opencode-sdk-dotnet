using System.Net;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Plugins;

public sealed class PluginsClientExtensionsTests
{
    private readonly FixtureLoader _fixtures = new();

    [Test]
    public async Task WaitForPluginsAsync_Should_Refuse_NoThrow_Before_Any_Request()
    {
        using var scenario = ContractScenario.Responding(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(WireBodyData.EmptyPluginList),
        });

        var failure = Assert.Throws<ArgumentException>(() => _ = scenario.Client.Plugins.WaitForPluginsAsync(
            requestOptions: OpenCodeRequestOptions.NoThrow));

        await Assert.That(failure.ParamName).IsEqualTo("requestOptions");
        await Assert.That(failure.Message).Contains("Plugin waits always throw API errors; NoThrow is not supported.");
        await Assert.That(scenario.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WaitForPluginsAsync_Should_Skip_The_Plugin_Poll_On_A_Mocking_Instance()
    {
        var client = new FakePlugins();

        var wait = client.WaitForPluginsAsync();

        await Assert.That(wait.Status).IsEqualTo(TaskStatus.RanToCompletion);
        await wait;
        await Assert.That(client.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task WaitForPluginsAsync_Should_Use_The_Pipeline_Clock()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var polls = 0;
        using var scenario = PipelineScenario.Responding(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(++polls == 1
                ? WireBodyData.EmptyPluginList : _fixtures.LoadJson("LanguageModels.one-plugin-list.json")),
        }, networkTimeout: Timeout.InfiniteTimeSpan, time: time);
        var client = new PluginsClient(scenario.Pipeline);

        await new ManualClockWaits(time).ConsumeWaitsAsync(
            token => client.WaitForPluginsAsync(cancellationToken: token), [TimeSpan.FromMilliseconds(25)], caller);

        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin", "/api/plugin"], CollectionOrdering.Matching);
        await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(0);
    }

    private sealed class FakePlugins : PluginsClient
    {
        public int Calls { get; private set; }

        public override Task<PluginListResponse> ListPluginsAsync(PluginListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<PluginListResponse>(new InvalidOperationException("The mocking instance must not list plugins."));
        }
    }
}
