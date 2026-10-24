using System.Net;
using OpenCode.Sdk.Tests.Transport.Support;

namespace OpenCode.Sdk.Tests.Support;

public sealed class PipelineScenarioTests
{
    [Test]
    public async Task Dispose_Should_Keep_The_Triple_Alive_Until_Explicit_Disposal()
    {
        using var scenario = PipelineScenario.Responding(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(WireBodyData.InfoOk),
        });
        var adapter = new RecordingResponseAdapter();

        await ExecuteAsync(adapter);

        await Assert.That(adapter.AdaptedRawBody).IsEqualTo(WireBodyData.InfoOk);
        await Assert.That(scenario.IsDisposed).IsFalse();

        var secondAdapter = new RecordingResponseAdapter();
        await ExecuteAsync(secondAdapter);
        await Assert.That(secondAdapter.AdaptedRawBody).IsEqualTo(WireBodyData.InfoOk);

        scenario.Dispose();

        await Assert.That(scenario.IsDisposed).IsTrue();
        await Assert.That(() => ExecuteAsync(adapter)).Throws<ObjectDisposedException>();

        async Task ExecuteAsync(RecordingResponseAdapter responseAdapter) =>
            _ = await scenario.Pipeline.ExecuteAsync(nameof(PipelineScenario), HttpMethod.Get, "/api/info", responseAdapter,
                options: null, CancellationToken.None);
    }
}
