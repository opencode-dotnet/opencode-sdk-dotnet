using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The blocking deterministic model-session workflow (ADR-0022): a scripted turn driven end to
/// end through the real pinned server in simulation mode. It runs inside the normal test gate
/// like every other test - no separate lane, no skip - because a simulated model turn needs no
/// credentials and no outbound network.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SimulatedSessionWorkflowTests(SimulatedDriveServerFixture server)
{
    private const string ScriptedReply = "Hello from the drive.";

    /// <summary>
    /// The single model the fixture's provider seed registers
    /// (<c>SimulatedDriveServerFixture.SimulationConfig</c>): named once here so the session's
    /// model selection and the invocation assertion that checks it cannot drift apart.
    /// </summary>
    private const string SimulatedModelId = "sim-model";

    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(60);

    [Test]
    [Timeout(180_000)]
    public async Task SessionPrompt_Should_Round_A_Scripted_Model_Turn_Through_The_Real_Server(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });

        var sessionId = await CreateSimulatedSessionAsync(client, workspace.Path, "simulated-session-workflow", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);

        using var eventWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        eventWindow.CancelAfter(TimeSpan.FromSeconds(120));
        var deltas = new List<string>();
        var observed = new List<string>();
        var sawCompletion = false;
        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventTask = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var @event in client.Events.SubscribeAsync(eventWindow.Token))
                    {
                        // The first received event - the server's connected event - proves the SSE
                        // subscription is attached before the prompt fires, so the scripted deltas
                        // are observed by construction, never by racing the subscription.
                        _ = subscribed.TrySetResult(true);
                        observed.Add(@event.Type);
                        if (@event is SessionTextDelta delta && delta.Data.SessionId == sessionId)
                        {
                            deltas.Add(delta.Data.Delta);
                        }

                        if (@event is SessionExecutionSucceeded done && done.Data.SessionId == sessionId)
                        {
                            sawCompletion = true;
                            return;
                        }
                    }
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The subscription's own window expired rather than the test being torn down:
                    // report it as the bounded wait it is, and name what did arrive, so a missing
                    // terminal event reads as "the turn never completed" with its own evidence
                    // instead of an opaque cancellation.
                    throw new TimeoutException(
                        $"The session never completed within the event window. Deltas: '{string.Concat(deltas)}'. Events: {string.Join(", ", observed)}.",
                        exception);
                }
            },
            cancellationToken);

        // Proves the subscription is attached before the prompt fires, and fails loudly the
        // moment the reader ends first: a dead subscription must surface as its own failure,
        // never as a later "no deltas arrived" timeout that hides the cause (review I7).
        var attached = await Task.WhenAny(subscribed.Task, eventTask);
        if (attached == eventTask)
        {
            await eventTask; // surfaces the subscription failure instead of a later timeout
            throw new InvalidOperationException("The event subscription ended before its first event.");
        }

        _ = await session.PromptAsync(
            new SessionPromptRequest { Text = "hello simulated model" },
            cancellationToken: cancellationToken);

        var invocation = await DriveAsync(
            () => server.Controller.WaitForRequestAsync(RequestWait), "waiting for the model request");
        await AssertSimulatedInvocationAsync(invocation);

        await DriveAsync(
            () => server.Controller.ChunkTextAsync(invocation.Id, "Hello ", "from ", "the drive."),
            "scripting the reply chunks");
        await DriveAsync(() => server.Controller.FinishAsync(invocation.Id), "finishing the model turn");

        await eventTask;
        await Assert.That(sawCompletion).IsTrue();
        await Assert.That(string.Concat(deltas)).IsEqualTo(ScriptedReply);

        await AssertPersistedReplyAsync(session, cancellationToken);
    }

    [Test]
    [Timeout(180_000)]
    public async Task SessionInterrupt_Should_Clean_Up_The_Pending_Invocation(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });

        var sessionId = await CreateSimulatedSessionAsync(client, workspace.Path, "simulated-session-interrupt", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);

        _ = await session.PromptAsync(
            new SessionPromptRequest { Text = "interrupt me" }, cancellationToken: cancellationToken);
        _ = await DriveAsync(
            () => server.Controller.WaitForRequestAsync(RequestWait), "waiting for the model request");

        // The event bus has no replay, so the subscription must be attached - and proven attached,
        // the same way the round-trip test above proves it - before the interrupt is posted.
        using var eventWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        eventWindow.CancelAfter(TimeSpan.FromSeconds(60));
        var observed = new List<string>();
        var sawInterrupted = false;
        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventTask = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var @event in client.Events.SubscribeAsync(eventWindow.Token))
                    {
                        _ = subscribed.TrySetResult(true);
                        observed.Add(@event.Type);
                        if (@event is SessionExecutionInterrupted interrupted
                            && interrupted.Data.SessionId == sessionId
                            && interrupted.Data.Reason == SessionExecutionInterruptedDataReason.User)
                        {
                            sawInterrupted = true;
                            return;
                        }
                    }
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The subscription's own window expired rather than the test being torn down:
                    // report it as the bounded wait it is, with what did arrive, instead of an
                    // opaque cancellation.
                    throw new TimeoutException(
                        $"The session never reported session.execution.interrupted within the event window. Events: {string.Join(", ", observed)}.",
                        exception);
                }
            },
            cancellationToken);

        var attached = await Task.WhenAny(subscribed.Task, eventTask);
        if (attached == eventTask)
        {
            await eventTask; // surfaces the subscription failure instead of a later timeout
            throw new InvalidOperationException("The event subscription ended before its first event.");
        }

        _ = await session.InterruptAsync(cancellationToken: cancellationToken);

        // Upstream publishes session.execution.interrupted only after the interrupted execution
        // fiber's finalizers have fully settled - which include the simulated provider's own
        // acquireRelease/close finalizer that removes the invocation from state.pending
        // (run-coordinator.ts:57-61: the "settled" callback runs for every exit, including
        // interruption, "after the final drain and before the execution settles"; execution.ts:
        // 119-135: that same settled callback is what publishes Execution.Interrupted;
        // session-event.ts:233-238: the wire shape this SDK generates as
        // SessionExecutionInterrupted). So once this event for this session id arrives, the
        // removal has already happened server-side, and a single post-event read suffices -
        // no poll needed.
        await eventTask;
        await Assert.That(sawInterrupted).IsTrue();

        var pending = await DriveAsync(server.Controller.PendingCountAsync, "reading the pending invocations");
        await Assert.That(pending).IsEqualTo(0);
    }

    [Test]
    [Timeout(180_000)]
    public async Task EventSubscription_Should_Surface_Caller_Cancellation(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));

        _ = await Assert.That(async () =>
        {
            await foreach (var _ in client.Events.SubscribeAsync(cancellation.Token))
            {
                // The stream is drained only so the enumerator observes the cancellation; the
                // events themselves are this test's noise, not its subject.
            }
        }).Throws<OperationCanceledException>();
    }

    /// <summary>
    /// The named checkpoint: the prompt reached the drive through the config-seeded provider.
    /// Both halves are load-bearing. The url proves the seed pinned the chat route the simulated
    /// network claims, but it does not discriminate on its own - every openai-chat-shaped
    /// provider claims that same route - so a background model call the server made for its own
    /// reasons would satisfy it, and the scripted turn would then be driven against the wrong
    /// invocation while the real one sat pending until the event window expired. The model id is
    /// what identifies this as the seeded provider's request for this prompt.
    /// </summary>
    private static async Task AssertSimulatedInvocationAsync(DriveInvocation invocation)
    {
        await Assert.That(invocation.Url).IsEqualTo(SimulationConfigSeed.ChatCompletionsUrl);
        await Assert.That(invocation.Model).IsEqualTo(SimulatedModelId);
    }

    /// <summary>
    /// Creates a session pinned to the config-seeded simulated model. In simulation the drive
    /// backend answers only the chat route this provider claims, so the explicit
    /// <see cref="ModelRef"/> is what makes every prompt in the suite deterministic rather than
    /// dependent on whichever catalog model the server would otherwise default to. The location is
    /// named in the body because session creation without a parent takes it from there alone: without
    /// one the server binds the session to its own working directory
    /// (<c>packages/server/src/handlers/session.ts:136-138</c> at the pin), whatever the client's
    /// location header says.
    /// </summary>
    private static async Task<string> CreateSimulatedSessionAsync(
        OpenCodeClient client, string directory, string title, CancellationToken cancellationToken)
    {
        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = title,
                Location = new LocationPublicRef { Directory = directory },
                Model = new ModelRef { Id = SimulatedModelId, ProviderId = "sim" },
            },
            cancellationToken: cancellationToken);
        return created.Session.Id;
    }

    /// <summary>
    /// Reads the turn back through the generated message operations: the scripted reply must be
    /// durable server state, not only a stream artifact the test happened to observe.
    /// </summary>
    private static async Task AssertPersistedReplyAsync(SessionClient session, CancellationToken cancellationToken)
    {
        var messages = await session.ListMessagesAsync(cancellationToken: cancellationToken);
        var assistant = messages.Messages.OfType<SessionMessageAssistant>().Single();
        var text = assistant.Content.OfType<SessionMessageAssistantText>().Single();
        await Assert.That(text.Text).IsEqualTo(ScriptedReply);
    }

    /// <summary>
    /// Every <see cref="DriveController"/> wait is bounded by both its own timeout and the
    /// controller's lifetime token, so a fixture teardown that races an in-flight wait surfaces
    /// as <see cref="OperationCanceledException"/> rather than <see cref="TimeoutException"/>.
    /// Both mean the same thing to a workflow test - the drive never answered - so both are
    /// reported as one loud, unambiguous failure instead of one of them escaping as an
    /// unrelated cancellation that a runner could read as a skipped or cancelled test.
    /// </summary>
    private static async Task<T> DriveAsync<T>(Func<Task<T>> operation, string description)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException($"The drive controller was torn down while {description}.", exception);
        }
    }

    private static async Task DriveAsync(Func<Task> operation, string description)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException($"The drive controller was torn down while {description}.", exception);
        }
    }
}
