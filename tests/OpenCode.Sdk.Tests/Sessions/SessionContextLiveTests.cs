using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// Compaction checkpoints delimit active context without deleting the transcript; one-shot
/// generation previews that context without admitting another conversation message.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionContextLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(180_000)]
    public async Task CompactAsync_Should_Persist_The_Summary_When_A_Completed_Conversation_Is_Compacted(
        CancellationToken cancellationToken)
    {
        const string summary = "## Objective\nPersist the owned conversation summary.";
        await WithOwnedSessionAsync(async (client, sessionId, sessionCleanup, cleanup) =>
        {
            var session = client.Sessions.GetSessionClient(sessionId);
            var probe = CreateProbe(cleanup, cancellationToken, out var reader);
            _ = await CompleteConversationAsync(client, sessionId, cancellationToken);
            await ConnectProbeAsync(client, reader, probe, cancellationToken);

            var inputId = await CompactConversationAsync(client, sessionId, sessionCleanup, summary, cancellationToken);

            // Inspect finite history before assuming that a compaction-completed event exists.
            var messages = await ReadHistoryAsync(session, cancellationToken);
            var compactions = messages.OfType<SessionMessageCompactionCompleted>().Where(message => message.Id == inputId).ToList();
            await Assert.That(compactions).Count().IsEqualTo(1);
            await Assert.That(compactions[0].Summary).IsEqualTo(summary);
            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var ended = await probe.WaitForAsync<SessionCompactionEnded>(
                item => item.Data.SessionId == sessionId && item.Data.Text == summary,
                "owned compaction summary", barrier.Token);
            await Assert.That(ended.Data.Text).IsEqualTo(summary);
        }, cancellationToken);
    }

    [Test]
    [Timeout(180_000)]
    public async Task GenerateTextAsync_Should_Return_The_Scripted_Text_When_Generating_From_A_Session(
        CancellationToken cancellationToken)
    {
        using var generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        const string prompt = "Session generation context prompt";
        const string contextReply = "Session generation context reply.";
        const string generationPrompt = "Generate a preview without adding a conversation message";
        const string reply = "One-shot session generation reply.";
        await WithOwnedSessionAsync(async (client, sessionId, sessionCleanup, cleanup) =>
        {
            cleanup.Own("generation cancellation", _ => generationCancellation.CancelOnWorkerAsync());
            var session = client.Sessions.GetSessionClient(sessionId);
            var before = await CompleteConversationAsync(client, sessionId, cancellationToken, prompt, contextReply);
            var projector = new SessionMessageEvidenceProjector();
            var evidence = projector.Project(before);

            var generation = session.GenerateTextAsync(new SessionGenerateRequest
            {
                Prompt = generationPrompt,
            }, cancellationToken: generationCancellation.Token);
            cleanup.Own("original generation request", generation,
                _ => generationCancellation.IsCancellationRequested);
            var invocation = new OwnedDriveInvocation(server.Controller,
                await server.Controller.WaitForRequestAsync(TimeSpan.FromSeconds(60)));
            cleanup.Own("generation invocation", invocation.DisconnectAsync);
            var messages = invocation.Invocation.Body.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() is "user" or "assistant").ToList();
            string?[] expectedRoles = ["user", "assistant", "user"];
            string?[] expectedTexts = [prompt, contextReply, generationPrompt];
            await Assert.That(messages.Select(message => message.GetProperty("role").GetString()))
                .IsEquivalentTo(expectedRoles, CollectionOrdering.Matching);
            await Assert.That(messages.Select(message => message.GetProperty("content").GetString()))
                .IsEquivalentTo(expectedTexts, CollectionOrdering.Matching);
            await invocation.ChunkTextAsync(reply);
            await invocation.FinishAsync();
            // Observe history after the request is terminal, even if a wrong route broke its body.
            _ = await Task.WhenAny(generation);
            var wait = client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: cancellationToken);
            sessionCleanup.RetainWait(wait);
            _ = await wait;

            var after = await ReadHistoryAsync(session, cancellationToken);
            await Assert.That(projector.Project(after)).IsEquivalentTo(evidence, CollectionOrdering.Matching);
            var generated = await generation;
            await Assert.That(generated.Generate.Text).IsEqualTo(reply);
        }, cancellationToken);
    }

    [Test]
    [Timeout(180_000)]
    public async Task GetContextAsync_Should_Exclude_Previous_Exchanges_When_A_Compaction_Has_Completed(
        CancellationToken cancellationToken)
    {
        const string summary = "## Objective\nUse the compacted context rather than the previous exchange.";
        await WithOwnedSessionAsync(async (client, sessionId, sessionCleanup, cleanup) =>
        {
            var session = client.Sessions.GetSessionClient(sessionId);
            var probe = CreateProbe(cleanup, cancellationToken, out var reader);
            var before = await CompleteConversationAsync(client, sessionId, cancellationToken);
            var original = before.Where(message => message is SessionMessageUser or SessionMessageAssistant).Select(message => message.Id).ToList();
            await Assert.That(original).Count().IsEqualTo(2);
            await ConnectProbeAsync(client, reader, probe, cancellationToken);
            var inputId = await CompactConversationAsync(client, sessionId, sessionCleanup, summary, cancellationToken);
            var full = await ReadHistoryAsync(session, cancellationToken);
            await Assert.That(full.OfType<SessionMessageCompactionCompleted>().Select(message => message.Id)).Contains(inputId);
            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            _ = await probe.WaitForAsync<SessionCompactionEnded>(
                item => item.Data.SessionId == sessionId && item.Data.Text == summary,
                "context compaction completed", barrier.Token);

            var response = await session.GetContextAsync(cancellationToken: cancellationToken);

            foreach (var id in original)
            {
                await Assert.That(response.Context.Select(message => message.Id)).DoesNotContain(id);
                await Assert.That(full.Select(message => message.Id)).Contains(id);
            }

            await Assert.That(response.Context).IsNotEmpty();
            await Assert.That(response.Context[0]).IsTypeOf<SessionMessageCompactionCompleted>();
            var checkpoint = (SessionMessageCompactionCompleted)response.Context[0];
            await Assert.That(checkpoint.Id).IsEqualTo(inputId);
            await Assert.That(checkpoint.Summary).IsEqualTo(summary);
        }, cancellationToken);
    }

    private async Task WithOwnedSessionAsync(
        Func<OpenCodeClient, string, OwnedSessionInboxCleanup, OwnedCleanup, Task> scenario,
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15));
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, cancellationToken);
        var sessionCleanup = new OwnedSessionInboxCleanup(client.Sessions.GetSessionClient(sessionId),
            client.Experimental, sessionId, server.Controller, TimeSpan.FromSeconds(15));
        Exception? primaryFailure = null;
        try
        {
            await scenario(client, sessionId, sessionCleanup, cleanup);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await CompleteCleanupAsync(cleanup, sessionCleanup, primaryFailure);
    }

    private static async Task<string> CreateOwnedSessionAsync(
        OpenCodeClient client,
        string directory,
        CancellationToken cancellationToken)
    {
        var request = new SessionCreateRequest
        {
            Location = new LocationPublicRef { Directory = directory },
            Model = new ModelRef { ProviderId = SimulationConfigSeed.ProviderId, Id = SimulationConfigSeed.ModelId },
            Title = "session-context-live",
        };
        var response = await client.Sessions.CreateSessionAsync(request, cancellationToken: cancellationToken);
        return response.Session.Id;
    }

    private static SessionEventProbe CreateProbe(
        OwnedCleanup cleanup,
        CancellationToken cancellationToken,
        out OwnedEventReader reader)
    {
        var readerStepTimeout = TimeSpan.FromSeconds(15);
        var owned = new OwnedEventReader(TimeSpan.FromSeconds(120), readerStepTimeout, cancellationToken);
        reader = owned;
        // Two inner steps can each spend a budget on the operation and another on cancellation.
        // Leave one further step's margin so the reader reports its own named failure first.
        cleanup.Own("context event reader", TimeSpan.FromTicks(readerStepTimeout.Ticks * 5), _ => owned.CompleteAsync(null));
        return new SessionEventProbe(owned);
    }

    private static async Task ConnectProbeAsync(
        OpenCodeClient client,
        OwnedEventReader reader,
        SessionEventProbe probe,
        CancellationToken cancellationToken)
    {
        probe.Start(client.Events.SubscribeAsync(reader.Token));
        await probe.WaitForConnectedAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ISessionMessageInfo>> CompleteConversationAsync(
        OpenCodeClient client,
        string sessionId,
        CancellationToken cancellationToken,
        string prompt = "Context coverage prompt",
        string reply = "Context coverage reply.")
    {
        var session = client.Sessions.GetSessionClient(sessionId);
        var turn = new SimulatedSessionTurn(server, client, session, sessionId);
        _ = await turn.CompleteAsync(prompt, reply, cancellationToken);
        _ = await client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: cancellationToken);
        return await ReadHistoryAsync(session, cancellationToken);
    }

    private async Task DriveReplyAsync(OwnedSessionInboxCleanup cleanup, string reply)
    {
        var invocation = cleanup.RetainInvocation(await server.Controller.WaitForRequestAsync(TimeSpan.FromSeconds(60)));
        await invocation.ChunkTextAsync(reply);
        await invocation.FinishAsync();
    }

    private async Task<string> CompactConversationAsync(
        OpenCodeClient client,
        string sessionId,
        OwnedSessionInboxCleanup cleanup,
        string summary,
        CancellationToken cancellationToken)
    {
        var inputId = "msg_compaction_" + Guid.NewGuid().ToString("N");
        _ = await client.Sessions.GetSessionClient(sessionId).CompactAsync(
            new SessionCompactRequest { Id = inputId }, cancellationToken: cancellationToken);
        await DriveReplyAsync(cleanup, summary);
        var wait = client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: cancellationToken);
        cleanup.RetainWait(wait);
        _ = await wait;
        return inputId;
    }

    private static async Task<IReadOnlyList<ISessionMessageInfo>> ReadHistoryAsync(SessionClient session, CancellationToken cancellationToken)
    {
        var response = await session.ListMessagesAsync(new SessionMessageListRequest { Order = ListOrder.Ascending },
            cancellationToken: cancellationToken);
        return response.Messages;
    }

    private static async Task CompleteCleanupAsync(
        OwnedCleanup cleanup,
        OwnedSessionInboxCleanup sessionCleanup,
        Exception? primaryFailure)
    {
        try
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await sessionCleanup.CompleteAsync(primaryFailure);
    }
}
