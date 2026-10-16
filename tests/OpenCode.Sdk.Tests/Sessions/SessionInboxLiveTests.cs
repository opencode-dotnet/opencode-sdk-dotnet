using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionInboxLiveTests(SimulatedDriveServerFixture server)
{
    private const string ChatCompletionsUrl = "https://api.openai.com/v1/chat/completions";
    private const string ModelId = "sim-model";
    private const string ProviderId = "sim";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(60);

    [Test]
    [Timeout(180_000)]
    public async Task DeleteInboxCancelAsync_Should_Remove_A_Queued_Prompt_While_PostWaitAsync_Completes_The_Held_Turn(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, "session-inbox-cancel", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionInboxCleanup(session, client.Experimental, sessionId, server.Controller, CleanupTimeout);
        var suffix = Guid.NewGuid().ToString("N");
        var promptA = "task-seven cancel active prompt " + suffix;
        var replyA = "Task seven cancel active reply " + suffix + ".";
        var inboxId = "msg_task7_cancel_" + suffix;
        var promptB = "task-seven queued cancellation prompt " + suffix;
        Exception? primaryFailure = null;

        try
        {
            var pendingBefore = await server.Controller.PendingInvocationIdsAsync();
            _ = await session.PromptAsync(
                new SessionPromptRequest { Text = promptA },
                cancellationToken: cancellationToken);
            var invocationA = cleanup.RetainInvocation(
                await server.Controller.WaitForRequestAsync(RequestWait));

            await RequireSimulatedInvocationAsync(invocationA.Invocation);

            var active = await client.Sessions.GetActiveAsync(cancellationToken: cancellationToken);
            await Assert.That(active.Status).IsEqualTo(200);
            await Assert.That(active.IsError).IsFalse();
            await Assert.That(active.Active.ContainsKey(sessionId)).IsTrue();
            await Assert.That(active.Active[sessionId].Type).IsEqualTo("running");

            var admittedB = await session.PromptAsync(
                new SessionPromptRequest
                {
                    Id = inboxId,
                    Text = promptB,
                    Delivery = SessionInboxDelivery.Queue,
                    Resume = false,
                },
                cancellationToken: cancellationToken);
            await AssertPromptAsync(admittedB, sessionId, inboxId, promptB, SessionInboxDelivery.Queue);
            var pending = await session.ListInboxAsync(cancellationToken: cancellationToken);
            await AssertPendingPromptAsync(pending, sessionId, inboxId, promptB, SessionInboxDelivery.Queue);

            var wait = client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: cancellationToken);
            cleanup.RetainWait(wait);

            var cancelled = await session.CancelInboxAsync(inboxId, cancellationToken: cancellationToken);
            await AssertNoContentAsync(cancelled);
            var repeated = await session.CancelInboxAsync(
                inboxId,
                OpenCodeRequestOptions.NoThrow,
                cancellationToken);
            await AssertNoContentAsync(repeated);
            var afterCancellation = await session.ListInboxAsync(cancellationToken: cancellationToken);
            await AssertInboxAbsentAsync(afterCancellation, inboxId);
            // Only the held turn is pending (the queued prompt was not dispatched); read what this test added.
            await Assert.That((await server.Controller.PendingInvocationIdsAsync()).Except(pendingBefore, StringComparer.Ordinal)).IsEquivalentTo([invocationA.Invocation.Id]);

            await invocationA.ChunkTextAsync(replyA);
            await invocationA.FinishAsync();
            var waited = await wait;
            await AssertNoContentAsync(waited);

            var log = await ReadFiniteLogAsync(session, cancellationToken);
            await AssertCancellationLogAsync(log, sessionId, inboxId, promptB, replyA);
            var finalInbox = await session.ListInboxAsync(cancellationToken: cancellationToken);
            await AssertInboxAbsentAsync(finalInbox, inboxId);

            Console.WriteLine(
                "session-inbox-cancel-live: active=running enqueue=queue cancel=204 repeat=204 wait=" +
                Number(waited.Status));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task PostInboxSteerAsync_Should_Change_And_Deliver_A_Queued_Prompt_After_The_Active_Step(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, "session-inbox-steer", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionInboxCleanup(session, client.Experimental, sessionId, server.Controller, CleanupTimeout);
        var suffix = Guid.NewGuid().ToString("N");
        var promptA = "task-seven steer active prompt " + suffix;
        var replyA = "Task seven steer first reply " + suffix + ".";
        var inboxId = "msg_task7_steer_" + suffix;
        var promptB = "task-seven queued steer prompt " + suffix;
        var replyB = "Task seven steer second reply " + suffix + ".";
        Exception? primaryFailure = null;

        try
        {
            var pendingBefore = await server.Controller.PendingInvocationIdsAsync();
            _ = await session.PromptAsync(
                new SessionPromptRequest { Text = promptA },
                cancellationToken: cancellationToken);
            var invocationA = cleanup.RetainInvocation(
                await server.Controller.WaitForRequestAsync(RequestWait));

            await RequireSimulatedInvocationAsync(invocationA.Invocation);

            var admittedB = await session.PromptAsync(
                new SessionPromptRequest
                {
                    Id = inboxId,
                    Text = promptB,
                    Delivery = SessionInboxDelivery.Queue,
                    Resume = false,
                },
                cancellationToken: cancellationToken);
            await AssertPromptAsync(admittedB, sessionId, inboxId, promptB, SessionInboxDelivery.Queue);
            var queued = await session.ListInboxAsync(cancellationToken: cancellationToken);
            await AssertPendingPromptAsync(queued, sessionId, inboxId, promptB, SessionInboxDelivery.Queue);

            var steered = await session.UpdateInboxAsync(inboxId, new SessionInboxUpdateRequest { Delivery = SessionInboxDelivery.Steer }, cancellationToken: cancellationToken);
            await AssertNoContentAsync(steered);
            var changed = await session.ListInboxAsync(cancellationToken: cancellationToken);
            await AssertPendingPromptAsync(changed, sessionId, inboxId, promptB, SessionInboxDelivery.Steer);
            var conflict = await session.UpdateInboxAsync(
                inboxId,
                new SessionInboxUpdateRequest { Delivery = SessionInboxDelivery.Steer },
                OpenCodeRequestOptions.NoThrow,
                cancellationToken);
            await AssertConflictAsync(conflict, inboxId);
            // Only the held turn is pending (the queued prompt was not dispatched); read what this test added.
            await Assert.That((await server.Controller.PendingInvocationIdsAsync()).Except(pendingBefore, StringComparer.Ordinal)).IsEquivalentTo([invocationA.Invocation.Id]);

            await invocationA.ChunkTextAsync(replyA);
            await invocationA.FinishAsync();
            var invocationB = cleanup.RetainInvocation(
                await server.Controller.WaitForRequestAsync(RequestWait));

            await RequireSimulatedInvocationAsync(invocationB.Invocation);
            await invocationB.ChunkTextAsync(replyB);
            await invocationB.FinishAsync();
            var waited = await client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: cancellationToken);
            await AssertNoContentAsync(waited);

            var log = await ReadFiniteLogAsync(session, cancellationToken);
            await AssertSteerLogAsync(log, sessionId, inboxId, promptB, replyA, replyB);
            var finalInbox = await session.ListInboxAsync(cancellationToken: cancellationToken);
            await AssertInboxAbsentAsync(finalInbox, inboxId);

            Console.WriteLine(
                "session-inbox-steer-live: enqueue=queue change=steer delivered=true conflict=409 wait=" +
                Number(waited.Status));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
    }

    private static async Task<string> CreateOwnedSessionAsync(
        OpenCodeClient client,
        string directory,
        string title,
        CancellationToken cancellationToken)
    {
        var response = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = title,
                Location = new LocationPublicRef { Directory = directory },
                Model = new ModelRef { Id = ModelId, ProviderId = ProviderId },
            },
            cancellationToken: cancellationToken);
        return response.Session.Id;
    }

    private static async Task<List<ISessionLogItem>> ReadFiniteLogAsync(
        SessionClient session,
        CancellationToken cancellationToken)
    {
        var transcript = new SessionLogTranscript(session);
        return await transcript.ReadToEndAsync(
            new SessionLogRequest { Follow = false },
            cancellationToken);
    }

    private static async Task AssertPromptAsync(
        SessionPromptResponse response,
        string sessionId,
        string inboxId,
        string text,
        SessionInboxDelivery delivery)
    {
        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Prompt.Id).IsEqualTo(inboxId);
        await Assert.That(response.Prompt.SessionId).IsEqualTo(sessionId);
        await Assert.That(response.Prompt.Payload.Text).IsEqualTo(text);
        await Assert.That(response.Prompt.Delivery).IsEqualTo(delivery);
    }

    private static async Task AssertPendingPromptAsync(
        SessionInboxListResponse response,
        string sessionId,
        string inboxId,
        string text,
        SessionInboxDelivery delivery)
    {
        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        var rows = response.Inbox.OfType<SessionInboxUser>()
            .Where(item => item.Id == inboxId)
            .ToList();
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].SessionId).IsEqualTo(sessionId);
        await Assert.That(rows[0].Payload.Text).IsEqualTo(text);
        await Assert.That(rows[0].Delivery).IsEqualTo(delivery);
    }

    private static async Task AssertInboxAbsentAsync(SessionInboxListResponse response, string inboxId)
    {
        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Inbox.OfType<SessionInboxUser>().Any(item => item.Id == inboxId)).IsFalse();
    }

    private static async Task AssertConflictAsync(OpenCodeResponse response, string inboxId)
    {
        await Assert.That(response.Status).IsEqualTo(409);
        await Assert.That(response.Error).IsTypeOf<ConflictError>();
        var conflict = response.Error as ConflictError;
        await Assert.That(conflict?.Tag).IsEqualTo("ConflictError");
        await Assert.That(conflict?.Resource).IsEqualTo(inboxId);
        await Assert.That(conflict?.Message).Contains(inboxId);
    }

    private static async Task AssertNoContentAsync(OpenCodeResponse response)
    {
        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
    }

    private static async Task AssertCancellationLogAsync(
        IReadOnlyList<ISessionLogItem> items,
        string sessionId,
        string inboxId,
        string prompt,
        string reply)
    {
        var enqueued = FindIndex(items, 0, item =>
            item is SessionInboxEnqueued { Data: var data }
            && data.SessionId == sessionId
            && data.InboxId == inboxId
            && data.Item is SessionInboxItemUser user
            && user.Delivery == SessionInboxDelivery.Queue
            && user.Payload.Text == prompt);
        var cancelled = FindIndex(items, enqueued + 1, item =>
            item is SessionInboxCancelled { Data: var data }
            && data.SessionId == sessionId
            && data.InboxId == inboxId);
        var textEnded = FindIndex(items, cancelled + 1, item =>
            item is SessionTextEnded { Data: var data }
            && data.SessionId == sessionId
            && data.Text == reply);
        var succeeded = FindIndex(items, textEnded + 1, item =>
            item is SessionExecutionSucceeded { Data.SessionId: var observed }
            && observed == sessionId);

        await Assert.That(enqueued).IsGreaterThanOrEqualTo(0);
        await Assert.That(cancelled).IsGreaterThan(enqueued);
        await Assert.That(textEnded).IsGreaterThan(cancelled);
        await Assert.That(succeeded).IsGreaterThan(textEnded);
        await Assert.That(items.OfType<SessionInboxDelivered>().Any(item =>
            item.Data.SessionId == sessionId && item.Data.InboxId == inboxId)).IsFalse();
    }

    private static async Task AssertSteerLogAsync(
        IReadOnlyList<ISessionLogItem> items,
        string sessionId,
        string inboxId,
        string prompt,
        string replyA,
        string replyB)
    {
        var enqueued = FindIndex(items, 0, item =>
            item is SessionInboxEnqueued { Data: var data }
            && data.SessionId == sessionId
            && data.InboxId == inboxId
            && data.Item is SessionInboxItemUser user
            && user.Delivery == SessionInboxDelivery.Queue
            && user.Payload.Text == prompt);
        var changed = FindIndex(items, enqueued + 1, item =>
            item is SessionInboxDeliveryChanged { Data: var data }
            && data.SessionId == sessionId
            && data.InboxId == inboxId
            && data.Delivery == SessionInboxDelivery.Steer);
        var delivered = FindIndex(items, changed + 1, item =>
            item is SessionInboxDelivered { Data: var data }
            && data.SessionId == sessionId
            && data.InboxId == inboxId);
        var textAEnded = FindIndex(items, 0, item =>
            item is SessionTextEnded { Data: var data }
            && data.SessionId == sessionId
            && data.Text == replyA);
        var textBEnded = FindIndex(items, delivered + 1, item =>
            item is SessionTextEnded { Data: var data }
            && data.SessionId == sessionId
            && data.Text == replyB);
        var succeeded = FindIndex(items, textBEnded + 1, item =>
            item is SessionExecutionSucceeded { Data.SessionId: var observed }
            && observed == sessionId);

        await Assert.That(enqueued).IsGreaterThanOrEqualTo(0);
        await Assert.That(changed).IsGreaterThan(enqueued);
        await Assert.That(delivered).IsGreaterThan(changed);
        await Assert.That(textAEnded).IsGreaterThanOrEqualTo(0);
        await Assert.That(textBEnded).IsGreaterThan(textAEnded);
        await Assert.That(textBEnded).IsGreaterThan(delivered);
        await Assert.That(succeeded).IsGreaterThan(textBEnded);
    }

    private static int FindIndex(
        IReadOnlyList<ISessionLogItem> items,
        int start,
        Func<ISessionLogItem, bool> predicate)
    {
        for (var index = Math.Max(0, start); index < items.Count; index++)
        {
            if (predicate(items[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task RequireSimulatedInvocationAsync(DriveInvocation invocation)
    {
        await Assert.That(invocation.Model).IsEqualTo(ModelId);
        await Assert.That(invocation.Url).IsEqualTo(ChatCompletionsUrl);
    }

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
