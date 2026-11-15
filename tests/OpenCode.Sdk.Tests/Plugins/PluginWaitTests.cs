using System.Net;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Plugins;

public sealed class PluginWaitTests
{
    private readonly FixtureLoader _fixtures = new();

    [Test]
    public async Task WaitAsync_Should_End_After_The_First_Non_Empty_Plugin_List()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var polls = 0;
        using var scenario = ContractScenario.Responding(_ => Answer(HttpStatusCode.OK,
            ++polls <= 2 ? WireBodyData.EmptyPluginList : _fixtures.LoadJson("LanguageModels.one-plugin-list.json")));

        await new ManualClockWaits(time).ConsumeWaitsAsync(
            token => new PluginWait(scenario.Client.Plugins, time).WaitAsync(null, null, token),
            [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25)], caller);

        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin", "/api/plugin", "/api/plugin"], CollectionOrdering.Matching);
        await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(0);
    }

    [Test]
    public async Task WaitAsync_Should_End_Only_After_More_Than_Five_Seconds_When_Lists_Stay_Empty()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var lists = 0;
        // The first list moves the clock past the bound: a clock that started before the first answer would end at once.
        using var scenario = ContractScenario.Responding(_ =>
        {
            if (++lists == 1)
            {
                time.Advance(TimeSpan.FromSeconds(6));
            }

            return Answer(HttpStatusCode.OK, WireBodyData.EmptyPluginList);
        });

        await new ManualClockWaits(time).ConsumeWaitsAsync(
            token => new PluginWait(scenario.Client.Plugins, time).WaitAsync(null, null, token),
            [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(25), 201)], caller);

        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            Enumerable.Repeat("/api/plugin", 202), CollectionOrdering.Matching);
        await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitAsync_Should_End_Without_Throwing_When_The_Plugin_List_Fails(bool transportFailure)
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(_ => transportFailure
            ? throw new HttpRequestException("Plugin list failed.") : Answer(HttpStatusCode.InternalServerError, WireBodyData.UnknownError));

        await new ManualClockWaits(time).EndBeforeAnyWaitAsync(
            token => new PluginWait(scenario.Client.Plugins, time).WaitAsync(null, null, token),
            operation => operation.WaitAsync(HangGuard.Bound, caller.Token), caller);

        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WaitAsync_Should_Preserve_The_Caller_Token_When_Cancelled_Between_Polls()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using var scenario = EmptyLists();
        await new ManualClockWaits(time).CancelBetweenPollsAsync(
            token => new PluginWait(scenario.Client.Plugins, time).WaitAsync(null, null, token), caller);

        await Assert.That(scenario.Requests.Single().RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/plugin"));
    }

    [Test]
    public async Task WaitAsync_Should_Pass_Request_And_Options_To_The_Plugin_List()
    {
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(_ => Answer(HttpStatusCode.OK, _fixtures.LoadJson("LanguageModels.one-plugin-list.json")));
        var request = new PluginListRequest { Location = new LocationSelector { Directory = "/query/dir" } };
        var options = new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/header/ç" } };

        await new PluginWait(scenario.Client.Plugins, new ManualTimeProvider()).WaitAsync(request, options, caller.Token)
            .WaitAsync(HangGuard.Bound);

        var recorded = scenario.Requests.Single();
        await Assert.That(recorded.RequestUri!.Query).IsEqualTo("?location[directory]=" + Uri.EscapeDataString("/query/dir"));
        await Assert.That(recorded.Headers).ContainsKey("x-opencode-directory");
        await Assert.That(recorded.Headers["x-opencode-directory"]).IsEqualTo(Uri.EscapeDataString("/header/ç"));
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static ContractScenario EmptyLists() => ContractScenario.Responding(_ => Answer(HttpStatusCode.OK, WireBodyData.EmptyPluginList));
}
