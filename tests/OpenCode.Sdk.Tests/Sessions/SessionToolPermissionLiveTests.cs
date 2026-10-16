using System.Globalization;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Sessions.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// One model-originated chain through the real server in simulation mode proving two actual
/// upstream mechanisms: a controller-backed Drive tool's progress and completion, whose real
/// result the model loop resumes with, and the builtin read tool's permission gate, which parks
/// execution until the SDK replies. Every barrier is a positive observation (a connected frame, a
/// model request carrying the prior tool result, a typed correlated event); order is asserted as
/// a per-call subsequence, never as adjacency or global counts.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionToolPermissionLiveTests(SimulatedDriveServerFixture server)
{
    private const string FinalText = "Both tools completed.";

    [Test]
    [Timeout(240_000)]
    public async Task SessionPrompt_Should_Complete_A_Drive_Tool_Then_A_Permission_Gated_Read_Through_The_Real_Server(
        CancellationToken cancellationToken)
    {
        var scenario = await OwnedToolScenario.CreateAsync(server, "session-tool-permission-live", cancellationToken);
        var nonce = "drive-" + Guid.NewGuid().ToString("N");
        await scenario.RunAsync(async () =>
        {
            var sessionId = scenario.SessionId;
            _ = await scenario.Session.PromptAsync(
                new SessionPromptRequest { Text = "use the tools" }, cancellationToken: cancellationToken);

            await CompleteDriveToolTurnAsync(scenario, sessionId, nonce);

            // Turn 2: the model loop resumed with the real tool result, and now calls builtin read.
            var second = await scenario.WaitForModelRequestAsync();
            await Assert.That(OwnedToolScenario.RequireToolResult(second, OwnedToolScenario.DriveCallId)).Contains(nonce);
            await scenario.ScriptToolCallAsync(second, OwnedToolScenario.ReadCallId, "read", new JsonObject { ["path"] = "." });

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var readCalled = await scenario.Probe.WaitForAsync<SessionToolCalled>(
                called => called.Data.SessionId == sessionId && called.Data.Id == OwnedToolScenario.ReadCallId,
                "session.tool.called for call_read", barrier.Token);
            var asked = await scenario.Probe.WaitForAsync<PermissionAsked>(
                asked => asked.Data.SessionId == sessionId && asked.Data.Source?.Id == OwnedToolScenario.ReadCallId,
                "permission.asked for call_read", barrier.Token);
            await Assert.That(asked.Data.Action).IsEqualTo(SimulationConfigSeed.ReadPermissionAction);
            await Assert.That(asked.Data.Resources).IsNotEmpty();
            await Assert.That(asked.Data.Source!.Type).IsEqualTo("tool");
            await Assert.That(asked.Data.Source.MessageId).IsEqualTo(readCalled.Data.AssistantMessageId);

            var reply = await scenario.Session.ReplyToPermissionAsync(
                asked.Data.Id,
                new SessionPermissionReplyRequest { Decision = PermissionReply.Once },
                cancellationToken: cancellationToken);
            await Assert.That(reply.Status).IsEqualTo(204);
            await Assert.That(reply.IsError).IsFalse();

            var replied = await scenario.Probe.WaitForAsync<PermissionReplied>(
                replied => replied.Data.SessionId == sessionId && replied.Data.RequestId == asked.Data.Id,
                "permission.replied for the read request", barrier.Token);
            await Assert.That(replied.Data.Reply).IsEqualTo(PermissionReply.Once);
            _ = await scenario.Probe.WaitForAsync<SessionToolSuccess>(
                success => success.Data.SessionId == sessionId && success.Data.Id == OwnedToolScenario.ReadCallId,
                "session.tool.success for call_read", barrier.Token);

            // Turn 3: the model loop resumed with the real read result, and finishes with text.
            var third = await scenario.WaitForModelRequestAsync();
            await Assert.That(OwnedToolScenario.RequireToolResult(third, OwnedToolScenario.ReadCallId)).IsNotEmpty();
            await scenario.ScriptFinalTextAsync(third, FinalText);
            _ = await scenario.Probe.WaitForAsync<SessionTextEnded>(
                ended => ended.Data.SessionId == sessionId && ended.Data.Text == FinalText,
                "session.text.ended with the final text", barrier.Token);
            _ = await scenario.Probe.WaitForAsync<SessionExecutionSucceeded>(
                succeeded => succeeded.Data.SessionId == sessionId,
                "session.execution.succeeded", barrier.Token);
            scenario.MarkTerminal();

            await AssertCausalSubsequencesAsync(scenario.Probe.Snapshot(), sessionId, nonce, asked.Data.Id);
            Console.WriteLine(
                "tool-permission-live: mode=owned arms=drive-progress+success,read-permission-once,final-text session=" +
                sessionId + " reply-status=" + reply.Status.ToString(CultureInfo.InvariantCulture));
        });
    }

    /// <summary>
    /// Turn 1: the model calls the owned Drive tool, the controller observes the invocation with
    /// its exact identities and input, reports progress, and completes it with the echoed nonce.
    /// </summary>
    private static async Task CompleteDriveToolTurnAsync(OwnedToolScenario scenario, string sessionId, string nonce)
    {
        var first = await scenario.WaitForModelRequestAsync();
        await scenario.ScriptToolCallAsync(
            first, OwnedToolScenario.DriveCallId, OwnedToolScenario.ToolName, new JsonObject { ["value"] = nonce });

        var tool = await scenario.WaitForToolInvocationAsync();
        await Assert.That(tool.Name).IsEqualTo(OwnedToolScenario.ToolName);
        await Assert.That(tool.Input.GetProperty("value").GetString()).IsEqualTo(nonce);
        await Assert.That(tool.SessionId).IsEqualTo(sessionId);
        await Assert.That(tool.Agent).IsEqualTo(SimulationConfigSeed.ReadAskAgentId);
        await Assert.That(tool.MessageId).IsNotEmpty();
        await Assert.That(tool.CallId).IsEqualTo(OwnedToolScenario.DriveCallId);

        await scenario.UpdateToolAsync(tool, 0, new JsonObject { ["phase"] = "working", ["nonce"] = nonce });
        await scenario.FinishToolAsync(tool, new JsonObject { ["echo"] = nonce }, "echo:" + nonce);
    }

    /// <summary>
    /// Per-call order as a subsequence: unrelated events may interleave, but each call's own
    /// lifecycle must appear in this order, the Drive call carrying the nonce through progress and
    /// success, and neither call may have failed.
    /// </summary>
    private static async Task AssertCausalSubsequencesAsync(
        IReadOnlyList<IEvent> events, string sessionId, string nonce, string permissionRequestId)
    {
        var owned = events.Where(candidate => SessionIdOf(candidate) == sessionId).ToList();
        await Assert.That(owned.OfType<SessionToolFailed>()
            .Select(failed => failed.Data.Id + ": " + failed.Data.Error.Message)).IsEmpty();

        await AssertToolLifecycleOrderAsync(owned, OwnedToolScenario.DriveCallId);
        var driveCalled = IndexOf<SessionToolCalled>(owned, item => item.Data.Id == OwnedToolScenario.DriveCallId);
        var driveProgress = IndexOf<SessionToolProgress>(owned, item => item.Data.Id == OwnedToolScenario.DriveCallId);
        var driveSucceeded = IndexOf<SessionToolSuccess>(owned, item => item.Data.Id == OwnedToolScenario.DriveCallId);
        await Assert.That(driveCalled).IsLessThan(driveProgress);
        await Assert.That(driveProgress).IsLessThan(driveSucceeded);
        var progress = owned.OfType<SessionToolProgress>().Single(item => item.Data.Id == OwnedToolScenario.DriveCallId);
        await Assert.That(progress.Data.Metadata["nonce"].GetString()).IsEqualTo(nonce);
        var driveSuccess = owned.OfType<SessionToolSuccess>().Single(item => item.Data.Id == OwnedToolScenario.DriveCallId);
        await Assert.That(driveSuccess.Data.Content.OfType<ToolTextContent>().Select(text => text.Text))
            .Contains("echo:" + nonce);

        await AssertToolLifecycleOrderAsync(owned, OwnedToolScenario.ReadCallId);
        var readCalled = IndexOf<SessionToolCalled>(owned, called => called.Data.Id == OwnedToolScenario.ReadCallId);
        var asked = IndexOf<PermissionAsked>(owned, asked => asked.Data.Id == permissionRequestId);
        var replied = IndexOf<PermissionReplied>(owned, replied => replied.Data.RequestId == permissionRequestId);
        var readSuccess = IndexOf<SessionToolSuccess>(owned, success => success.Data.Id == OwnedToolScenario.ReadCallId);
        await Assert.That(readCalled).IsLessThan(asked);
        await Assert.That(asked).IsLessThan(replied);
        await Assert.That(replied).IsLessThan(readSuccess);

        var textEnded = IndexOf<SessionTextEnded>(owned, ended => ended.Data.Text == FinalText);
        var succeeded = IndexOf<SessionExecutionSucceeded>(owned, _ => true);
        await Assert.That(textEnded).IsLessThan(succeeded);
    }

    /// <summary>One call's own lifecycle in arrival order: input started, input ended, called, success.</summary>
    private static async Task AssertToolLifecycleOrderAsync(List<IEvent> owned, string callId)
    {
        var started = IndexOf<SessionToolInputStarted>(owned, item => item.Data.Id == callId);
        var ended = IndexOf<SessionToolInputEnded>(owned, item => item.Data.Id == callId);
        var called = IndexOf<SessionToolCalled>(owned, item => item.Data.Id == callId);
        var success = IndexOf<SessionToolSuccess>(owned, item => item.Data.Id == callId);
        await Assert.That(started).IsLessThan(ended);
        await Assert.That(ended).IsLessThan(called);
        await Assert.That(called).IsLessThan(success);
    }

    /// <summary>The arrival index of the first matching event; a missing event fails naming what was sought.</summary>
    private static int IndexOf<T>(List<IEvent> owned, Func<T, bool> predicate)
        where T : IEvent
    {
        var index = owned.FindIndex(candidate => candidate is T typed && predicate(typed));
        return index >= 0
            ? index
            : throw new InvalidOperationException(
                $"The owned session observed no {typeof(T).Name} matching the expected correlation.");
    }

    private static string? SessionIdOf(IEvent @event) => @event switch
    {
        SessionToolInputStarted started => started.Data.SessionId,
        SessionToolInputEnded ended => ended.Data.SessionId,
        SessionToolCalled called => called.Data.SessionId,
        SessionToolProgress progress => progress.Data.SessionId,
        SessionToolSuccess success => success.Data.SessionId,
        SessionToolFailed failed => failed.Data.SessionId,
        PermissionAsked asked => asked.Data.SessionId,
        PermissionReplied replied => replied.Data.SessionId,
        SessionTextEnded textEnded => textEnded.Data.SessionId,
        SessionExecutionSucceeded succeeded => succeeded.Data.SessionId,
        _ => null,
    };
}
