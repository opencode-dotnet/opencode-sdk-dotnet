using System.Text.Json.Nodes;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// The owned Drive tool's failure and interruption arms through the real server: a failed tool
/// reaches the SDK as a correlated failure and the model loop resumes with that real error; an
/// interrupted session cancels the held invocation (the exact <c>tool.cancel</c>), fails the tool
/// as aborted, and settles as a user interruption, after which the backend refuses a late finish.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionToolLifecycleLiveTests(SimulatedDriveServerFixture server)
{
    private const string RecoveryText = "Recovered after the tool failure.";

    [Test]
    [Timeout(240_000)]
    public async Task SessionPrompt_Should_Resume_With_The_Real_Error_When_The_Drive_Tool_Fails(
        CancellationToken cancellationToken)
    {
        var scenario = await OwnedToolScenario.CreateAsync(server, "session-tool-failure-live", cancellationToken);
        var failureMessage = "drive failure " + Guid.NewGuid().ToString("N");
        await scenario.RunAsync(async () =>
        {
            var sessionId = scenario.SessionId;
            _ = await scenario.Session.PromptAsync(
                new SessionPromptRequest { Text = "use the failing tool" }, cancellationToken: cancellationToken);

            var first = await scenario.WaitForModelRequestAsync();
            await scenario.ScriptToolCallAsync(
                first, OwnedToolScenario.DriveCallId, OwnedToolScenario.ToolName, new JsonObject { ["value"] = "fail" });
            var tool = await scenario.WaitForToolInvocationAsync();
            await Assert.That(tool.SessionId).IsEqualTo(sessionId);
            await Assert.That(tool.CallId).IsEqualTo(OwnedToolScenario.DriveCallId);

            await scenario.FailToolAsync(tool, failureMessage);

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var failed = await scenario.Probe.WaitForAsync<SessionToolFailed>(
                failed => failed.Data.SessionId == sessionId && failed.Data.Id == OwnedToolScenario.DriveCallId,
                "session.tool.failed for call_drive", barrier.Token);
            await Assert.That(failed.Data.Error.Message).Contains(failureMessage);

            // The model loop resumed with the real error as the tool result for that call.
            var second = await scenario.WaitForModelRequestAsync();
            await Assert.That(OwnedToolScenario.RequireToolResult(second, OwnedToolScenario.DriveCallId))
                .Contains(failureMessage);
            await scenario.ScriptFinalTextAsync(second, RecoveryText);
            _ = await scenario.Probe.WaitForAsync<SessionTextEnded>(
                ended => ended.Data.SessionId == sessionId && ended.Data.Text == RecoveryText,
                "session.text.ended with the recovery text", barrier.Token);
            _ = await scenario.Probe.WaitForAsync<SessionExecutionSucceeded>(
                succeeded => succeeded.Data.SessionId == sessionId,
                "session.execution.succeeded", barrier.Token);
            scenario.MarkTerminal();

            Console.WriteLine(
                "tool-lifecycle-live: mode=owned arm=tool-fail-then-recover session=" + sessionId +
                " error-type=" + failed.Data.Error.Type);
        });
    }

    [Test]
    [Timeout(240_000)]
    public async Task SessionInterrupt_Should_Cancel_The_Held_Drive_Tool_And_Refuse_A_Late_Finish(
        CancellationToken cancellationToken)
    {
        var scenario = await OwnedToolScenario.CreateAsync(server, "session-tool-interrupt-live", cancellationToken);
        await scenario.RunAsync(async () =>
        {
            var sessionId = scenario.SessionId;
            _ = await scenario.Session.PromptAsync(
                new SessionPromptRequest { Text = "use the tool and get interrupted" },
                cancellationToken: cancellationToken);

            var first = await scenario.WaitForModelRequestAsync();
            await scenario.ScriptToolCallAsync(
                first, OwnedToolScenario.DriveCallId, OwnedToolScenario.ToolName, new JsonObject { ["value"] = "hold" });
            var tool = await scenario.WaitForToolInvocationAsync();
            await Assert.That(tool.SessionId).IsEqualTo(sessionId);
            await Assert.That(tool.CallId).IsEqualTo(OwnedToolScenario.DriveCallId);

            // The invocation is positively held (never settled) when the session is interrupted.
            var interrupt = await scenario.Session.InterruptAsync(cancellationToken: cancellationToken);
            await Assert.That(interrupt.Status).IsEqualTo(200);
            await Assert.That(interrupt.IsError).IsFalse();
            await Assert.That(interrupt.Interrupt.Interrupted).IsTrue();

            var cancellation = await scenario.WaitForToolCancellationAsync();
            await Assert.That(cancellation.Id).IsEqualTo(tool.Id);
            await Assert.That(cancellation.Reason).IsEqualTo("interrupted");
            scenario.ReleaseTool(tool);

            var waited = await scenario.WaitAsync(cancellationToken: cancellationToken);
            await Assert.That(waited.Status).IsEqualTo(204);

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var failed = await scenario.Probe.WaitForAsync<SessionToolFailed>(
                failed => failed.Data.SessionId == sessionId && failed.Data.Id == OwnedToolScenario.DriveCallId,
                "session.tool.failed (aborted) for call_drive", barrier.Token);
            await Assert.That(failed.Data.Error.Type).IsEqualTo("aborted");
            await Assert.That(failed.Data.Error.Message).IsEqualTo("Tool execution interrupted");
            var interrupted = await scenario.Probe.WaitForAsync<SessionExecutionInterrupted>(
                interrupted => interrupted.Data.SessionId == sessionId,
                "session.execution.interrupted", barrier.Token);
            await Assert.That(interrupted.Data.Reason).IsEqualTo(SessionExecutionInterruptedDataReason.User);
            scenario.MarkTerminal();

            // A late settlement of the cancelled invocation is refused with the backend's own
            // message. The payload deliberately differs from any settled one, so an identical
            // retry's silent success cannot be mistaken for this refusal.
            var refusal = await Assert.That(async () => await scenario.FinishToolAsync(
                tool, new JsonObject { ["echo"] = "late" }, "late")).Throws<InvalidOperationException>();
            await Assert.That(refusal!.Message)
                .Contains("Simulated tool invocation not found or already finished: " + tool.Id);

            Console.WriteLine(
                "tool-lifecycle-live: mode=owned arm=interrupt-cancel-wait-late-finish session=" + sessionId +
                " tool=" + tool.Id);
        });
    }
}
