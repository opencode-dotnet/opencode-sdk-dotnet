using System.Globalization;
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

    /// <summary>The eleven waits between twelve attempts: one second, doubling, at most five.</summary>
    private static readonly TimeSpan[] EveryWait = [.. new[] { 1, 2, 4, 5, 5, 5, 5, 5, 5, 5, 5 }.Select(static seconds => TimeSpan.FromSeconds(seconds))];

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
    public async Task WaitForDefaultAsync_Should_Ask_Again_After_Each_Unavailable_Answer()
    {
        var time = new ManualTimeProvider();
        var requests = 0;
        using var scenario = ContractScenario.Responding(_ => ++requests switch
        {
            1 => Answer(HttpStatusCode.ServiceUnavailable, WireBodyData.PluginActivationUnavailableError),
            2 => Answer(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError),
            _ => Answer(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel)),
        });

        var wait = await PassWaitsAsync(
            time, () => new DefaultModelWait(scenario.Client.LanguageModels, time).WaitAsync(null, null, CancellationToken.None), [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]).WaitAsync(HangGuard.Bound);
        var response = await Assert.That(wait.WaitAsync(HangGuard.Bound)).ThrowsNothing();

        await Assert.That(response!.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(scenario.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Surface_The_Last_Unavailable_Answer_After_Twelve_Attempts()
    {
        var time = new ManualTimeProvider();
        var requests = 0;
        using var scenario = ContractScenario.Responding(_ => Answer(HttpStatusCode.ServiceUnavailable, UnavailableBody(++requests)));

        var wait = await PassWaitsAsync(
            time, () => new DefaultModelWait(scenario.Client.LanguageModels, time).WaitAsync(null, null, CancellationToken.None), EveryWait).WaitAsync(HangGuard.Bound);
        using var decided = new CancellationTokenSource();
        var anotherWait = time.WaitForArmedOrCanceledAsync(EveryWait[^1], HangGuard.Bound, decided.Token);
        Task first;
        try
        {
            first = await Task.WhenAny(wait, anotherWait).WaitAsync(HangGuard.Bound);
        }
        finally
        {
            await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound);
            _ = await Task.WhenAny(anotherWait).WaitAsync(HangGuard.Bound);
            _ = anotherWait.Exception;
        }

        await Assert.That(first).IsSameReferenceAs(wait);
        var failure = await Assert.That(wait.WaitAsync(HangGuard.Bound)).Throws<OpenCodeApiException>();
        var error = await Assert.That(failure!.Error).IsTypeOf<ServiceUnavailableError>();
        await Assert.That(error!.Message).IsEqualTo("attempt 12");
        await Assert.That(time.ArmedIn(EveryWait[^1])).IsEqualTo(0);
        await Assert.That(scenario.Requests.Count).IsEqualTo(12);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Surface_Another_Failure_At_Once()
    {
        var requests = 0;
        using var scenario = ContractScenario.Responding(_ => ++requests == 1
            ? Answer(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError)
            : Answer(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel)));

        var failure = await Assert.That(scenario.Client.LanguageModels.WaitForDefaultAsync().WaitAsync(HangGuard.Bound))
            .Throws<OpenCodeApiException>();

        await Assert.That(failure!.Error).IsTypeOf<InvalidRequestError>();
        await Assert.That(scenario.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Stop_When_Cancelled_Between_Attempts()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var client = new UnavailableLanguageModels();

        var wait = new DefaultModelWait(client, time).WaitAsync(null, null, caller.Token);
        using var decided = new CancellationTokenSource();
        var created = time.WaitForTimerAsync(HangGuard.Bound, decided.Token);
        var first = await Task.WhenAny(wait, created).WaitAsync(HangGuard.Bound);
        await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound);
        _ = await Task.WhenAny(created).WaitAsync(HangGuard.Bound);
        _ = created.Exception;
        await Assert.That(first).IsSameReferenceAs(created);
        await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound);

        await Assert.That(time.ArmedIn(EveryWait[0])).IsEqualTo(0);
        var failure = await Assert.That(wait.WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
        await Assert.That(failure!.CancellationToken == caller.Token).IsTrue();
        await Assert.That(client.Tokens).IsEquivalentTo([caller.Token], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WaitForDefaultAsync_Should_Refuse_NoThrow_At_The_Call()
    {
        using var scenario = ContractScenario.Responding(_ => Answer(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel)));

        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.LanguageModels.WaitForDefaultAsync(requestOptions: OpenCodeRequestOptions.NoThrow));

        await Assert.That(scenario.Requests.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Starts the operation and passes each wait between its attempts: waits in real time until the
    /// next wait is created or the operation ends, requires the wait, checks its length, and only then
    /// moves the clock. The losing waiter is cancelled and observed.
    /// </summary>
    private static async Task<Task<ModelDefaultResponse>> PassWaitsAsync(ManualTimeProvider time, Func<Task<ModelDefaultResponse>> start, TimeSpan[] waits)
    {
        var operation = start();
        using (var decided = new CancellationTokenSource())
        {
            foreach (var expected in waits)
            {
                var created = time.WaitForTimerAsync(HangGuard.Bound, decided.Token);
                var first = await Task.WhenAny(operation, created).WaitAsync(HangGuard.Bound);
                if (first != created)
                {
                    await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound);
                    _ = await Task.WhenAny(created).WaitAsync(HangGuard.Bound);
                    _ = created.Exception;
                    _ = operation.Exception;
                }

                await Assert.That(first).IsSameReferenceAs(created);
                await Assert.That(time.ArmedIn(expected)).IsEqualTo(1);
                time.Advance(expected);
            }
        }

        return operation;
    }

    private static string UnavailableBody(int attempt) => string.Format(
        CultureInfo.InvariantCulture, "{{\"_tag\":\"ServiceUnavailableError\",\"message\":\"attempt {0}\",\"service\":\"plugin\"}}", attempt);

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

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
