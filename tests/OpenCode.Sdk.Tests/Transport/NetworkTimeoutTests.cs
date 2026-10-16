using System.Threading.Channels;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

/// <summary>
/// The network timeout as a caller sets it: the client's value bounds an ordinary call, a
/// per-call value wins either way, and a long-poll operation runs with no window unless the call
/// sets one, because its server answers only when the work ends. The server here never answers,
/// so the window alone decides when the exchange is cut; the clock is the test's, and each check
/// reads the token the exchange was sent under, which the window cancels the moment it passes.
/// </summary>
public sealed class NetworkTimeoutTests
{
    private const string OrdinaryOperation = "test.operation";

    private const string LongPollOperation = "session.shell";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(100);

    private static readonly TimeSpan ClientWindow = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ShortCallWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LongCallWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    [Test]
    public async Task An_Ordinary_Call_Should_Be_Cut_When_The_Default_Window_Passes()
    {
        using var scenario = await HangingScenario.StartAsync(OrdinaryOperation, clientWindow: null, callWindow: null);

        await scenario.AssertCutExactlyAtAsync(DefaultWindow);
    }

    [Test]
    public async Task An_Ordinary_Call_Should_Be_Cut_When_The_Client_Window_Passes()
    {
        using var scenario = await HangingScenario.StartAsync(OrdinaryOperation, ClientWindow, callWindow: null);

        await scenario.AssertCutExactlyAtAsync(ClientWindow);
    }

    [Test]
    public async Task A_Shorter_Call_Window_Should_Win_Over_The_Client_Window()
    {
        using var scenario = await HangingScenario.StartAsync(OrdinaryOperation, ClientWindow, ShortCallWindow);

        await scenario.AssertCutExactlyAtAsync(ShortCallWindow);
    }

    [Test]
    public async Task A_Longer_Call_Window_Should_Win_Over_The_Client_Window()
    {
        using var scenario = await HangingScenario.StartAsync(OrdinaryOperation, ClientWindow, LongCallWindow);

        await scenario.AssertCutExactlyAtAsync(LongCallWindow);
    }

    [Test]
    public async Task An_Infinite_Call_Window_Should_Lift_The_Window_Of_An_Ordinary_Call()
    {
        using var scenario = await HangingScenario.StartAsync(OrdinaryOperation, ClientWindow, Timeout.InfiniteTimeSpan);

        await scenario.AssertNeverCutAsync();
    }

    /// <summary>A long poll waits for work, so a client-wide window does not cut it; the caller's token ends it.</summary>
    [Test]
    public async Task A_Long_Poll_Should_Outlast_The_Client_Window_Until_The_Caller_Cancels()
    {
        using var scenario = await HangingScenario.StartAsync(LongPollOperation, ClientWindow, callWindow: null);

        await scenario.AssertNeverCutAsync();
    }

    [Test]
    public async Task A_Long_Poll_Should_Outlast_The_Default_Window_Until_The_Caller_Cancels()
    {
        using var scenario = await HangingScenario.StartAsync(LongPollOperation, clientWindow: null, callWindow: null);

        await scenario.AssertNeverCutAsync();
    }

    [Test]
    public async Task A_Long_Poll_Should_Be_Cut_When_Its_Call_Window_Passes()
    {
        using var scenario = await HangingScenario.StartAsync(LongPollOperation, ClientWindow, ShortCallWindow);

        await scenario.AssertCutExactlyAtAsync(ShortCallWindow);
    }

    [Test]
    public async Task The_Long_Polls_Should_Be_The_Operations_That_Hold_Their_Headers_Until_The_Work_Ends()
    {
        await Assert.That(NetworkTimeouts.LongPolls).IsEquivalentTo(
        [
            "experimental.session.wait",
            "session.shell",
            "experimental.generate.text",
            "session.generate",
            "session.command",
        ]);
    }

    /// <summary>Minus one millisecond is <see cref="Timeout.InfiniteTimeSpan"/> itself, so the negative case is minus two.</summary>
    [Test]
    [Arguments(0L)]
    [Arguments(-TimeSpan.TicksPerMillisecond * 2)]
    [Arguments((int.MaxValue * TimeSpan.TicksPerMillisecond) + 1)]
    public async Task Options_Should_Refuse_A_Window_No_Timer_Can_Run(long ticks)
    {
        var value = TimeSpan.FromTicks(ticks);

        var client = await Assert.That(() => new OpenCodeClientOptions { NetworkTimeout = value }).Throws<ArgumentOutOfRangeException>();
        var call = await Assert.That(() => new OpenCodeRequestOptions { NetworkTimeout = value }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(client!.ParamName).IsEqualTo("value");
        await Assert.That(call!.ParamName).IsEqualTo("value");
    }

    [Test]
    [Arguments(1L)]
    [Arguments(int.MaxValue * TimeSpan.TicksPerMillisecond)]
    [Arguments(-TimeSpan.TicksPerMillisecond)]
    public async Task Options_Should_Accept_A_Window_A_Timer_Can_Run(long ticks)
    {
        var value = TimeSpan.FromTicks(ticks);

        await Assert.That(new OpenCodeClientOptions { NetworkTimeout = value }.NetworkTimeout).IsEqualTo(value);
        await Assert.That(new OpenCodeRequestOptions { NetworkTimeout = value }.NetworkTimeout).IsEqualTo(value);
    }

    /// <summary>
    /// A server that never answers, a pipeline on a manual clock, and one call already waiting
    /// under its window. The handler hands back the token the exchange was sent under; the window
    /// cancels it synchronously when it passes, so it is the exact witness of a cut.
    /// </summary>
    private sealed class HangingScenario : IDisposable
    {
        private readonly NeverAnsweringHandler _handler;
        private readonly HttpClient _httpClient;
        private readonly Pipeline _pipeline;
        private readonly ManualTimeProvider _time;
        private readonly CancellationTokenSource _caller;
        private readonly Task _call;
        private readonly CancellationToken _sentUnder;

        private HangingScenario(NeverAnsweringHandler handler, HttpClient httpClient, Pipeline pipeline, ManualTimeProvider time,
            CancellationTokenSource caller, Task call, CancellationToken sentUnder)
        {
            _handler = handler;
            _httpClient = httpClient;
            _pipeline = pipeline;
            _time = time;
            _caller = caller;
            _call = call;
            _sentUnder = sentUnder;
        }

        public static async Task<HangingScenario> StartAsync(string operationId, TimeSpan? clientWindow, TimeSpan? callWindow)
        {
            var time = new ManualTimeProvider();
            var handler = new NeverAnsweringHandler();
            var httpClient = new HttpClient(handler, disposeHandler: false);
            var pipeline = PipelineFactory.Create(httpClient, networkTimeout: clientWindow, time: time);
            var caller = new CancellationTokenSource();
            var options = callWindow is null ? null : new OpenCodeRequestOptions { NetworkTimeout = callWindow };
            var call = pipeline.ExecuteAsync(operationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, caller.Token);
            var sentUnder = await handler.WaitForSendAsync(HangGuard.Bound);
            return new HangingScenario(handler, httpClient, pipeline, time, caller, call, sentUnder);
        }

        /// <summary>One tick before the window the exchange is whole; at the window it is cut and the call fails as a transport failure.</summary>
        public async Task AssertCutExactlyAtAsync(TimeSpan window)
        {
            _time.Advance(window - OneTick);
            await Assert.That(_sentUnder.IsCancellationRequested).IsFalse();
            _time.Advance(OneTick);

            await Assert.That(_sentUnder.IsCancellationRequested).IsTrue();
            await Assert.That(_call.WaitAsync(HangGuard.Bound, CancellationToken.None)).Throws<OpenCodeTransportException>();
        }

        /// <summary>Far past every finite window the exchange is whole, and only the caller's token ends it.</summary>
        public async Task AssertNeverCutAsync()
        {
            _time.Advance(TimeSpan.FromTicks(LongCallWindow.Ticks * 100));
            await Assert.That(_sentUnder.IsCancellationRequested).IsFalse();
            await _caller.CancelOnWorkerAsync();

            await Assert.That(_call.WaitAsync(HangGuard.Bound, CancellationToken.None)).Throws<OperationCanceledException>();
        }

        public void Dispose()
        {
            _caller.Cancel();
            _pipeline.Dispose();
            _httpClient.Dispose();
            _handler.Dispose();
            _caller.Dispose();
        }
    }

    /// <summary>Answers nothing until the exchange is cancelled, as a server busy with long work does, and reports the token it was sent under.</summary>
    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        private readonly Channel<CancellationToken> _received = Channel.CreateUnbounded<CancellationToken>();

        /// <summary>Completes with the token of the first exchange the handler receives.</summary>
        public Task<CancellationToken> WaitForSendAsync(TimeSpan hangGuard) => _received.Reader.ReadAsync().AsTask().WaitAsync(hangGuard);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = _received.Writer.TryWrite(cancellationToken);

            // The answer is never given, so only the exchange's token ends the wait.
            var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
