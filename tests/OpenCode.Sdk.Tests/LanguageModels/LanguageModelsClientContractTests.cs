using System.Net;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.LanguageModels;

public sealed class LanguageModelsClientContractTests
{
    private const string KnownModel = "{\"id\":\"anthropic/claude-3-5-sonnet\",\"modelID\":\"claude-3-5-sonnet\","
        + "\"providerID\":\"anthropic\",\"name\":\"Claude 3.5 Sonnet\","
        + "\"capabilities\":{\"tools\":true,\"input\":[\"text\"],\"output\":[\"text\"]},"
        + "\"variants\":[{\"id\":\"default\"}],\"time\":{\"released\":1710000000},"
        + "\"cost\":[{\"input\":3,\"output\":15,\"cache\":{\"read\":0.3,\"write\":3.75}}],"
        + "\"status\":\"active\",\"enabled\":true,\"limit\":{\"context\":200000,\"output\":8192}}";

    [Test]
    public async Task GetDefaultAsync_Should_Return_The_Typed_Default_Model()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel));

        var response = await scenario.Client.LanguageModels.GetDefaultAsync();

        await Assert.That(response.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(response.Default.ModelId).IsEqualTo("claude-3-5-sonnet");
        await Assert.That(response.Default.ProviderId).IsEqualTo("anthropic");
        await Assert.That(response.Default.Status).IsEqualTo(ModelInfoStatus.Active);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/model/default"));
    }

    [Test]
    public async Task GetDefaultAsync_Should_Return_Null_When_No_Model_Is_Selected()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("null"));

        var response = await scenario.Client.LanguageModels.GetDefaultAsync();

        await Assert.That(response.Default).IsNull();
    }

    [Test]
    public async Task ListModelsAsync_Should_Return_The_Typed_Models_With_Their_Location()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{KnownModel}]"));

        var response = await scenario.Client.LanguageModels.ListModelsAsync();

        await Assert.That(response.Models.Count).IsEqualTo(1);
        await Assert.That(response.Models[0].Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(response.Models[0].ModelId).IsEqualTo("claude-3-5-sonnet");
        await Assert.That(response.Models[0].ProviderId).IsEqualTo("anthropic");
        await Assert.That(response.Models[0].Status).IsEqualTo(ModelInfoStatus.Active);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/model"));
    }

    [Test]
    public async Task ListModelsAsync_Should_Return_An_Empty_List_With_Its_Location()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        var response = await scenario.Client.LanguageModels.ListModelsAsync();

        await Assert.That(response.Models.Count).IsEqualTo(0);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Read_Once_After_A_Non_Empty_Plugin_List()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var polls = 0;
        using var scenario = ContractScenario.Responding(request => AnswerWithModel(request,
            request.RequestUri!.AbsolutePath == "/api/plugin" && ++polls <= 2 ? WireBodyData.EmptyPluginList : WireBodyData.OnePluginList));

        var response = await ConsumeWaitsAsync(time,
            token => new DefaultModelWait(scenario.Client.LanguageModels, time).WaitAsync(null, null, token),
            [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25)], caller);

        await Assert.That(response.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin", "/api/plugin", "/api/plugin", "/api/model/default"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Read_Once_When_The_Plugin_List_Stays_Empty()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(request => AnswerWithModel(request, WireBodyData.EmptyPluginList));

        var response = await ConsumeWaitsAsync(time,
            token => new DefaultModelWait(scenario.Client.LanguageModels, time).WaitAsync(null, null, token),
            [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(25), 201)], caller);

        await Assert.That(response.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            Enumerable.Repeat("/api/plugin", 202).Append("/api/model/default"), CollectionOrdering.Matching);
        await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitForDefaultAsync_Should_Read_Once_When_The_Plugin_List_Fails(bool transportFailure)
    {
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/plugin" => transportFailure ? throw new HttpRequestException("Plugin list failed.") : Answer(HttpStatusCode.InternalServerError, WireBodyData.UnknownError),
            "/api/model/default" => Answer(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel)),
            _ => throw new InvalidOperationException("Unexpected request path."),
        });

        var response = await scenario.Client.LanguageModels.WaitForDefaultAsync(cancellationToken: caller.Token).WaitAsync(HangGuard.Bound);

        await Assert.That(response.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin", "/api/model/default"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Surface_A_Default_Unavailable_Answer_Without_Asking_Again()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/plugin" => Answer(HttpStatusCode.OK, WireBodyData.OnePluginList),
            "/api/model/default" => Answer(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError),
            _ => throw new InvalidOperationException("Unexpected request path."),
        });

        await AssertUnavailableWithoutWaitAsync(time,
            token => new DefaultModelWait(scenario.Client.LanguageModels, time).WaitAsync(null, null, token), caller);

        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin", "/api/model/default"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Skip_The_Plugin_Poll_On_A_Mocking_Instance()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var client = new UnavailableLanguageModels();

        await AssertUnavailableWithoutWaitAsync(time,
            token => new DefaultModelWait(client, time).WaitAsync(null, null, token), caller);

        await Assert.That(client.Tokens).IsEquivalentTo([caller.Token], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Surface_Another_Failure_At_Once()
    {
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/plugin" => Answer(HttpStatusCode.OK, WireBodyData.OnePluginList),
            "/api/model/default" => Answer(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError),
            _ => throw new InvalidOperationException("Unexpected request path."),
        });

        var failure = await Assert.That(scenario.Client.LanguageModels.WaitForDefaultAsync(cancellationToken: caller.Token).WaitAsync(HangGuard.Bound))
            .Throws<OpenCodeApiException>();

        await Assert.That(failure!.Error).IsTypeOf<InvalidRequestError>();
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
            ["/api/plugin", "/api/model/default"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Stop_When_Cancelled_Between_Plugin_Polls()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(request => AnswerWithModel(request, WireBodyData.EmptyPluginList));

        var wait = new DefaultModelWait(scenario.Client.LanguageModels, time).WaitAsync(null, null, caller.Token);
        using var decided = new CancellationTokenSource();
        var created = time.WaitForTimerAsync(HangGuard.Bound, decided.Token);
        try
        {
            var first = await Task.WhenAny(wait, created).WaitAsync(HangGuard.Bound);
            await Assert.That(first).IsSameReferenceAs(created);
            await created;
            await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(1);
            await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound);

            var failure = await Assert.That(wait.WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
            await Assert.That(failure!.CancellationToken == caller.Token).IsTrue();
            await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(0);
            await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsolutePath)).IsEquivalentTo(
                ["/api/plugin"], CollectionOrdering.Matching);
        }
        finally
        {
            // Cleanup observes both tasks even after their owners have cancelled.
            await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await ObserveAsync(created, wait);
        }
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Refuse_NoThrow_At_The_Call()
    {
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(_ => Answer(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel)));

        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.LanguageModels.WaitForDefaultAsync(requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: caller.Token));

        await Assert.That(scenario.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Pass_Request_And_Options_To_The_Poll_And_The_Read()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(request => AnswerWithModel(request, WireBodyData.OnePluginList));
        var response = await ConsumeWaitsAsync(time, token => new DefaultModelWait(scenario.Client.LanguageModels, time).WaitAsync(
            ForwardedRequest(), ForwardedOptions(), token), [], caller);
        await Assert.That(response.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(scenario.Requests.Count).IsEqualTo(2);
        foreach (var request in scenario.Requests)
        {
            await AssertLocationAsync(request);
        }
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Pass_Request_And_Options_Through_The_Public_Door()
    {
        using var caller = new CancellationTokenSource();
        using var scenario = ContractScenario.Responding(request => AnswerWithModel(request, WireBodyData.OnePluginList));
        var response = await scenario.Client.LanguageModels.WaitForDefaultAsync(
            ForwardedRequest(), ForwardedOptions(), caller.Token).WaitAsync(HangGuard.Bound);
        await Assert.That(response.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await AssertLocationAsync(scenario.Requests.Single(static request => request.RequestUri!.AbsolutePath == "/api/plugin"));
        await AssertLocationAsync(scenario.Requests.Single(static request => request.RequestUri!.AbsolutePath == "/api/model/default"));
    }

    private static async Task AssertUnavailableWithoutWaitAsync(ManualTimeProvider time, Func<CancellationToken, Task<ModelDefaultResponse>> start, CancellationTokenSource caller)
    {
        var operation = start(caller.Token);
        using var decided = new CancellationTokenSource();
        var created = time.WaitForTimerAsync(HangGuard.Bound, decided.Token);
        try
        {
            var first = await Task.WhenAny(operation, created).WaitAsync(HangGuard.Bound, caller.Token);
            await Assert.That(first).IsSameReferenceAs(operation);
            var failure = await Assert.That(operation.WaitAsync(HangGuard.Bound, caller.Token)).Throws<OpenCodeApiException>();
            await Assert.That(failure!.Error).IsTypeOf<ServiceUnavailableError>();
        }
        finally
        {
            // Cleanup observes the operation and its losing timer observer after cancellation.
            await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await ObserveAsync(operation, created);
        }
    }

    private static ModelDefaultRequest ForwardedRequest() => new() { Location = new LocationSelector { Directory = "/query/dir" } };

    private static OpenCodeRequestOptions ForwardedOptions() => new() { Location = new LocationSelector { Directory = "/header/ç" } };

    private static async Task AssertLocationAsync(RecordedRequest request)
    {
        await Assert.That(request.RequestUri!.Query).IsEqualTo("?location[directory]=" + Uri.EscapeDataString(ForwardedRequest().Location!.Directory!));
        await Assert.That(request.Headers).ContainsKey("x-opencode-directory");
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo(Uri.EscapeDataString(ForwardedOptions().Location!.Directory!));
    }

    private static async Task<ModelDefaultResponse> ConsumeWaitsAsync(ManualTimeProvider time, Func<CancellationToken, Task<ModelDefaultResponse>> start, TimeSpan[] waits, CancellationTokenSource caller)
    {
        Task<ModelDefaultResponse>? wait = null;
        Task? passing = null;
        try
        {
            passing = PassWaitsAsync(time, () => wait = start(caller.Token), waits);
            // This check has no cancellation token; its owner cancels the operation during cleanup.
            await passing.WaitAsync(HangGuard.Bound, CancellationToken.None);
            return await wait!.WaitAsync(HangGuard.Bound, caller.Token);
        }
        finally
        {
            // Cleanup observes both tasks even after their owner has cancelled.
            await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await ObserveAsync(passing, wait);
        }
    }

    private static async Task ObserveAsync(params Task?[] tasks)
    {
        var all = Task.WhenAll(tasks.OfType<Task>());
        // The owners have cancelled; this cleanup has no cancellation token of its own.
        _ = await Task.WhenAny(all).WaitAsync(HangGuard.Bound, CancellationToken.None);
        _ = all.Exception;
    }

    /// <summary>
    /// Starts the operation and passes each wait between its attempts: waits in real time until the
    /// next wait is created or the operation ends, requires the wait, checks its length, and only then
    /// moves the clock. The losing waiter is cancelled and observed. On a failed check, the caller
    /// cancels the operation and observes both it and this helper before the failure propagates.
    /// </summary>
    private static async Task PassWaitsAsync(ManualTimeProvider time, Func<Task<ModelDefaultResponse>> start, TimeSpan[] waits)
    {
        var operation = start();
        using (var decided = new CancellationTokenSource())
        {
            Task? created = null;
            try
            {
                foreach (var expected in waits)
                {
                    created = time.WaitForTimerAsync(HangGuard.Bound, decided.Token);
                    var first = await Task.WhenAny(operation, created).WaitAsync(HangGuard.Bound, decided.Token);
                    await Assert.That(first).IsSameReferenceAs(created);
                    await created;
                    await Assert.That(time.ArmedIn(expected)).IsEqualTo(1);
                    time.Advance(expected);
                }
            }
            finally
            {
                // The timer owner's cancellation ends a losing read; cleanup must still observe it.
                await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
                if (created is not null)
                {
                    _ = await Task.WhenAny(created).WaitAsync(HangGuard.Bound, CancellationToken.None);
                    _ = created.Exception;
                }
            }
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage AnswerWithModel(HttpRequestMessage request, string plugins) => request.RequestUri!.AbsolutePath switch
    {
        "/api/plugin" => Answer(HttpStatusCode.OK, plugins),
        "/api/model/default" => Answer(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel)),
        _ => throw new InvalidOperationException("Unexpected request path."),
    };

    private sealed class UnavailableLanguageModels : LanguageModelsClient
    {
        public List<CancellationToken> Tokens { get; } = [];

        public override Task<ModelDefaultResponse> GetDefaultAsync(ModelDefaultRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            return Task.FromException<ModelDefaultResponse>(new OpenCodeApiException("503", 503, new ServiceUnavailableError { Message = "unavailable" }, rawBody: null));
        }
    }
}
