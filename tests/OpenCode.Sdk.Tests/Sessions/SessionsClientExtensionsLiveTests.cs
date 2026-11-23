using System.Text.Json.Nodes;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Sessions.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionsClientExtensionsLiveTests(SimulatedDriveServerFixture server)
{
    private const string CallId = "call_subagent";

    [Test]
    [Timeout(240_000)]
    public async Task GetTotalCostAsync_Should_Walk_A_Real_Subagent_Family(CancellationToken cancellationToken)
    {
        var scenario = await OwnedToolScenario.CreateAsync(server, "session-cost-live", cancellationToken);
        await scenario.RunAsync(async () =>
        {
            var initial = await scenario.Session.GetAsync(cancellationToken: cancellationToken);
            using var client = server.CreateClient(new LocationSelector { Directory = initial.Session.Location.Directory });
            var prompt = "Report this child marker: " + Guid.NewGuid().ToString("N");
            _ = await scenario.Session.PromptAsync(new SessionPromptRequest { Text = "Delegate the marker report." },
                cancellationToken: cancellationToken);
            var first = await scenario.WaitForModelRequestAsync();
            await scenario.ScriptToolCallAsync(first, CallId, "subagent",
                new JsonObject { ["agent"] = "general", ["description"] = "Report child marker", ["prompt"] = prompt });

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var success = await scenario.Probe.WaitForAsync<SessionToolSuccess>(
                item => item.Data.SessionId == scenario.SessionId && item.Data.Id == CallId,
                "subagent tool success", barrier.Token);
            await Assert.That(success.Data.Metadata).IsNotNull();
            var childId = success.Data.Metadata!["sessionID"].GetString();
            await Assert.That(childId).IsNotNull();
            await Assert.That(success.Data.Content.OfType<ToolTextContent>().Single().Text).Contains("sessionID: " + childId);

            var pending = new[] { await scenario.WaitForModelRequestAsync(), await scenario.WaitForModelRequestAsync() };
            var root = pending.Single(IsRootContinuation);
            var child = pending.Single(invocation => !IsRootContinuation(invocation));
            await Assert.That(child.Body.GetRawText()).Contains(prompt);
            Console.WriteLine("session-cost-live: first-request-after-tool-is-root=" + IsRootContinuation(pending[0]));
            await scenario.ScriptFinalTextAsync(root, "Root waiting for the child notice.");
            var rootEnded = await scenario.Probe.WaitForAsync<SessionExecutionSucceeded>(
                item => item.Data.SessionId == scenario.SessionId, "first root completion", barrier.Token);
            await scenario.ScriptFinalTextAsync(child, "Child marker reported.");
            _ = await scenario.Probe.WaitForAsync<SessionExecutionSucceeded>(
                item => item.Data.SessionId == childId, "child completion", barrier.Token);
            var notice = await scenario.WaitForModelRequestAsync();
            await Assert.That(notice.Body.GetRawText()).Contains(childId!);
            await Assert.That(notice.Body.GetRawText()).Contains("Child marker reported.");
            await scenario.ScriptFinalTextAsync(notice, "Root received the child notice.");
            _ = await scenario.Probe.WaitForAsync<SessionExecutionSucceeded>(
                item => item.Data.SessionId == scenario.SessionId, "root notice completion", rootEnded, barrier.Token);
            _ = await client.Experimental.WaitForSessionAsync(childId!, cancellationToken: cancellationToken);
            _ = await scenario.WaitAsync(cancellationToken);
            scenario.MarkTerminal();

            var children = new List<SessionInfo>();
            await foreach (var session in client.Sessions.EnumerateSessionsAsync(new SessionListRequest
            {
                ParentId = SessionParentFilter.Of(scenario.SessionId),
            }, cancellationToken: cancellationToken))
            {
                children.Add(session);
            }

            await Assert.That(children.Select(static session => session.Id)).IsEquivalentTo([childId!], CollectionOrdering.Matching);
            await Assert.That(children.Single().ParentId).IsEqualTo(scenario.SessionId);
            var rootRead = await scenario.Session.GetAsync(cancellationToken: cancellationToken);
            var childRead = await client.Sessions.GetSessionClient(childId!).GetAsync(cancellationToken: cancellationToken);
            await Assert.That(await client.Sessions.GetTotalCostAsync(scenario.SessionId, cancellationToken: cancellationToken))
                .IsEqualTo(rootRead.Session.Cost + childRead.Session.Cost);
            await Assert.That(await client.Sessions.GetTotalCostAsync(childId!, cancellationToken: cancellationToken))
                .IsEqualTo(childRead.Session.Cost);
            Console.WriteLine("session-cost-live: requests=child-finished,root-notice-finished");
        });
    }

    private static bool IsRootContinuation(DriveInvocation invocation) =>
        invocation.Body.GetProperty("messages").EnumerateArray().Any(static message =>
            message.GetProperty("role").GetString() == "tool" && message.GetProperty("tool_call_id").GetString() == CallId);
}
