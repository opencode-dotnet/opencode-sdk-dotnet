using System.Net;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class ExplicitServerProbeTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4096");

    [Test]
    [Arguments(null, null)]
    [Arguments("pw", "Basic b3BlbmNvZGU6cHc=")]
    [Arguments("", "Basic b3BlbmNvZGU6")]
    public async Task GetInfoAsync_Should_Send_Basic_Auth_Only_When_A_Password_Is_Set(string? password, string? expected)
    {
        using var handler = new RecordingHttpHandler(_ => Reply());
        using var transport = new HttpClient(handler);

        var info = await new ExplicitServerProbe(transport, new ManualTimeProvider())
            .GetInfoAsync(Endpoint, password, CancellationToken.None);

        await Assert.That(info.Version).IsEqualTo("0.0.0-test");
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].Authorization).IsEqualTo(expected);
        await Assert.That(handler.Requests[0].RequestUri).IsEqualTo(new Uri("http://127.0.0.1:4096/api/info"));
        await Assert.That(handler.IsDisposed).IsFalse();
    }

    [Test]
    public async Task ResolveAsync_Should_Report_An_Unreachable_Server_When_The_Five_Second_Bound_Expires() =>
        await AssertHeldProbeAsync(cancelCaller: false, cancelBound: true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResolveAsync_Should_Propagate_The_Callers_Cancellation_Unwrapped(bool alsoCancelBound) =>
        await AssertHeldProbeAsync(cancelCaller: true, cancelBound: alsoCancelBound);

    [Test]
    public async Task GetInfoAsync_Should_Report_An_Undeclared_Success_As_An_Incompatible_Response()
    {
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var transport = new HttpClient(handler);

        await AssertProbeFailureAsync(new ExplicitServerProbe(transport, new ManualTimeProvider()),
            "Server at http://127.0.0.1:4096/ did not provide a compatible V2 health response: The opencode API returned undeclared success status 204.");
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("null", ResponseAdapter<OpenCodeResponse>.NullSuccessBodyMessage)]
    [Arguments("{", "The opencode API returned a malformed success body.")]
    public async Task GetInfoAsync_Should_Classify_The_Actual_Decoder_Failure(string body, string expected)
    {
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        using var transport = new HttpClient(handler);

        await AssertProbeFailureAsync(new ExplicitServerProbe(transport, new ManualTimeProvider()),
            "Server at http://127.0.0.1:4096/ did not provide a compatible V2 health response: " + expected);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    private static async Task AssertProbeFailureAsync(ExplicitServerProbe probe, string expected)
    {
        var failure = await Assert.That(probe.GetInfoAsync(Endpoint, password: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();
        await Assert.That(failure!.Message).IsEqualTo(expected);
        await Assert.That(failure.InnerException).IsTypeOf<OpenCodeTransportException>();
    }

    private static async Task AssertHeldProbeAsync(bool cancelCaller, bool cancelBound)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var handler = new RecordingHttpHandler(_ =>
        {
            entered.SetResult();
            release.Wait();
            return Reply();
        });
        using var transport = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        var time = new ManualTimeProvider();
        var boundInstant = time.GetUtcNow() + TimeSpan.FromSeconds(5);
        var probe = new ExplicitServerProbe(transport, time);
        // RecordingHttpHandler's responder is synchronous: only this dedicated thread waits for release.
        var pending = Task.Factory.StartNew(() => probe.GetInfoAsync(Endpoint, password: null, caller.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            // A zero-length mutant can finish before entering the handler; that is an early
            // completion verdict, not a hang while waiting for a request that will never arrive.
            _ = await Task.WhenAny(entered.Task, pending).WaitAsync(HangGuard.Bound);
            await Assert.That(pending.IsCompleted).IsFalse();
            time.Advance(TimeSpan.FromMilliseconds(4_999));
            await Assert.That(handler.CancellationTokens[0].IsCancellationRequested).IsFalse();
            await Assert.That(pending.IsCompleted).IsFalse();
            if (cancelCaller)
            {
                await caller.CancelOnWorkerAsync();
            }
            if (cancelBound)
            {
                time.Advance(TimeSpan.FromMilliseconds(1));
                await Assert.That(handler.CancellationTokens[0].IsCancellationRequested).IsTrue();
            }
            release.Set();

            if (cancelCaller)
            {
                var failure = await Assert.That(pending).Throws<OperationCanceledException>();
                await Assert.That(failure!.CancellationToken).IsEqualTo(caller.Token);
            }
            else
            {
                var failure = await Assert.That(pending).Throws<OpenCodeServerException>();
                await Assert.That(failure!.InnerException).IsTypeOf<OperationCanceledException>();
                await Assert.That(failure.Message).IsEqualTo(
                    "Could not reach server at http://127.0.0.1:4096/: " + failure.InnerException!.Message);
            }
        }
        finally
        {
            release.Set();
            await new OperationDeadlineScenario().DrainAsync(pending);
            await Assert.That(time.ArmedIn(boundInstant - time.GetUtcNow())).IsEqualTo(0);
        }
    }

    private static HttpResponseMessage Reply() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(WireBodyData.InfoOk, System.Text.Encoding.UTF8, "application/json"),
    };
}
