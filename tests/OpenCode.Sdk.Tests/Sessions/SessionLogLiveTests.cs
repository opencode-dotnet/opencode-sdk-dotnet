using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionLogLiveTests(SimulatedDriveServerFixture server)
{
    private const string ReplayPrompt = "task-three replay prompt";
    private const string ReplayReply = "Task three replay reply.";
    private const string FollowFirstPrompt = "task-three follow first prompt";
    private const string FollowFirstReply = "Task three follow first reply.";
    private const string FollowSecondPrompt = "task-three follow second prompt";
    private const string FollowSecondReply = "Task three follow second reply.";
    private const string PastTailPrompt = "task-three past-tail prompt";
    private const string PastTailReply = "Task three past-tail reply.";
    private const string SkippedPrompt = "task-three skipped opening prompt";
    private const string SkippedReply = "Task three skipped opening reply.";
    private const string SkippedFollowPrompt = "task-three skipped follow prompt";
    private const string SkippedFollowReply = "Task three skipped follow reply.";

    /// <summary>
    /// How far past the tail the unreachable-cursor proof reaches. A scripted turn commits single
    /// digits of events, so nothing this session does can bring the aggregate up to that cursor.
    /// </summary>
    private const long PastTailOffset = 1000;

    /// <summary>
    /// How far past the tail the suppression proof reaches. Small enough that the following turn
    /// crosses it, so the same attachment shows both halves: the events at or below the cursor are
    /// skipped, and delivery resumes once the aggregate overtakes it.
    /// </summary>
    private const long SkippedOffset = 3;

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [Test]
    [Timeout(180_000)]
    public async Task GetLogAsync_Should_Replay_A_Completed_Turn_And_Resume_Exclusively(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, "session-log-replay", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        Exception? primaryFailure = null;

        try
        {
            var turn = new SimulatedSessionTurn(server, client, session, sessionId);
            var terminal = await turn.CompleteAsync(ReplayPrompt, ReplayReply, cancellationToken);
            cleanup.MarkTurnCompleted();

            var transcript = new SessionLogTranscript(session);
            var replay = await transcript.ReadToEndAsync(
                new SessionLogRequest { Follow = QueryBoolean.False },
                cancellationToken);
            var markers = replay.OfType<EventLogSynced>().ToList();
            await Assert.That(markers).Count().IsEqualTo(1);
            await Assert.That(replay[^1]).IsSameReferenceAs(markers[0]);
            await Assert.That(markers[0].AggregateId).IsEqualTo(sessionId);
            await Assert.That(markers[0].Seq).IsNotNull();
            await Assert.That(replay.Take(replay.Count - 1).All(item => item is ISessionEventDurable)).IsTrue();
            await Assert.That(replay.OfType<SessionCreated>().Any(created => created.Data.SessionId == sessionId)).IsTrue();

            var ownedTexts = replay.OfType<SessionTextEnded>()
                .Where(text => text.Data.SessionId == sessionId && text.Data.Text == ReplayReply)
                .ToList();
            var terminalMatches = replay.OfType<SessionExecutionSucceeded>()
                .Where(succeeded => succeeded.Id == terminal.Id && succeeded.Durable.Seq == terminal.Durable.Seq)
                .ToList();
            await Assert.That(ownedTexts).Count().IsEqualTo(1);
            await Assert.That(terminalMatches).Count().IsEqualTo(1);
            var ownedText = ownedTexts[0];
            var ownedSucceeded = terminalMatches[0];
            var replayTextIndex = FindIndex(replay, ownedText.Id, ownedText.Durable.Seq);
            var replaySucceededIndex = FindIndex(replay, ownedSucceeded.Id, ownedSucceeded.Durable.Seq);
            await Assert.That(replaySucceededIndex).IsGreaterThan(replayTextIndex);
            var anchors = replay.OfType<SessionExecutionStarted>()
                .Where(started => started.Data.SessionId == sessionId && started.Durable.Seq < ownedText.Durable.Seq)
                .OrderByDescending(started => started.Durable.Seq)
                .ToList();
            await Assert.That(anchors).IsNotEmpty();
            var anchor = anchors[0];

            var resumed = await transcript.ReadToEndAsync(
                new SessionLogRequest
                {
                    After = anchor.Durable.Seq,
                    Follow = QueryBoolean.False,
                },
                cancellationToken);
            var resumedMarkers = resumed.OfType<EventLogSynced>().ToList();
            await Assert.That(resumedMarkers).Count().IsEqualTo(1);
            await Assert.That(resumed[^1]).IsSameReferenceAs(resumedMarkers[0]);
            await Assert.That(resumed.Take(resumed.Count - 1).All(item => item is ISessionEventDurable)).IsTrue();
            await Assert.That(resumed.Any(item => item is SessionExecutionStarted started && started.Id == anchor.Id)).IsFalse();
            var resumedTextIndex = FindIndex(resumed, ownedText.Id, ownedText.Durable.Seq);
            var resumedSucceededIndex = FindIndex(resumed, ownedSucceeded.Id, ownedSucceeded.Durable.Seq);
            await Assert.That(resumedTextIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(resumedSucceededIndex).IsGreaterThan(resumedTextIndex);
            await Assert.That(resumedMarkers[0].AggregateId).IsEqualTo(sessionId);
            await Assert.That(resumedMarkers[0].Seq).IsNotNull();
            await Assert.That(resumedMarkers[0].Seq!.Value).IsGreaterThanOrEqualTo(ownedSucceeded.Durable.Seq);

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
    public async Task GetLogAsync_Should_Follow_A_Turn_After_The_Synced_Marker(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, "session-log-follow", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        var transcript = new SessionLogTranscript(session);
        cleanup.Own("session log enumerator", transcript.DisposeAsync);
        Exception? primaryFailure = null;

        try
        {
            var firstTurn = new SimulatedSessionTurn(server, client, session, sessionId);
            _ = await firstTurn.CompleteAsync(FollowFirstPrompt, FollowFirstReply, cancellationToken);
            cleanup.MarkTurnCompleted();
            var finite = await transcript.ReadToEndAsync(
                new SessionLogRequest { Follow = QueryBoolean.False },
                cancellationToken);
            var finiteMarkers = finite.OfType<EventLogSynced>().ToList();
            await Assert.That(finiteMarkers).Count().IsEqualTo(1);
            await Assert.That(finite[^1]).IsSameReferenceAs(finiteMarkers[0]);
            await Assert.That(finiteMarkers[0].Seq).IsNotNull();

            await transcript.AttachAsync(finiteMarkers[0].Seq!.Value, cancellationToken);
            await Assert.That(transcript.Marker).IsNotNull();
            await Assert.That(transcript.Marker!.AggregateId).IsEqualTo(sessionId);
            await Assert.That(transcript.ReplayItems.OfType<SessionTextEnded>()
                .Any(text => text.Data.Text == FollowSecondReply)).IsFalse();

            cleanup.MarkTurnStarted();
            var followedTurn = new SimulatedSessionTurn(server, client, session, sessionId);
            var terminal = await followedTurn.CompleteAsync(
                FollowSecondPrompt,
                FollowSecondReply,
                cancellationToken);
            cleanup.MarkTurnCompleted();
            await transcript.ReadTurnAsync(sessionId, FollowSecondReply);

            await Assert.That(transcript.TextEnded).IsNotNull();
            await Assert.That(transcript.Succeeded).IsNotNull();
            await Assert.That(transcript.Succeeded!.Id).IsEqualTo(terminal.Id);
            await Assert.That(transcript.Succeeded.Durable.Seq).IsEqualTo(terminal.Durable.Seq);
            var textIndex = transcript.LiveItems.ToList().IndexOf(transcript.TextEnded!);
            var succeededIndex = transcript.LiveItems.ToList().IndexOf(transcript.Succeeded);
            await Assert.That(textIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(succeededIndex).IsGreaterThan(textIndex);

            Console.WriteLine(
                "session-log-follow-live: finite-watermark=" + Number(finiteMarkers[0].Seq!.Value) +
                " replay-before-marker=" + Number(transcript.ReplayItems.Count) +
                " followed-text-seq=" + Number(transcript.TextEnded!.Durable.Seq) +
                " followed-succeeded-seq=" + Number(transcript.Succeeded.Durable.Seq));
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

    /// <summary>
    /// A cursor the aggregate has never reached is accepted rather than refused, and the reply is
    /// the marker alone carrying the real watermark, a value below the cursor that was sent. The
    /// same session's settled read is the control: it returns the turn, so the empty answer is the
    /// cursor being out of range rather than a server that retained nothing.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task GetLogAsync_Should_Answer_A_Cursor_Past_The_Tail_With_The_Marker_Alone(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, "session-log-past-tail", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        Exception? primaryFailure = null;

        try
        {
            var transcript = new SessionLogTranscript(session);
            var turn = new SimulatedSessionTurn(server, client, session, sessionId);
            var terminal = await turn.CompleteAsync(PastTailPrompt, PastTailReply, cancellationToken);
            cleanup.MarkTurnCompleted();

            var settled = await transcript.ReadToEndAsync(
                new SessionLogRequest { Follow = QueryBoolean.False }, cancellationToken);
            await Assert.That(settled.OfType<SessionExecutionSucceeded>()
                .Any(succeeded => succeeded.Durable.Seq == terminal.Durable.Seq)).IsTrue();
            var watermark = settled.OfType<EventLogSynced>().Single().Seq;
            await Assert.That(watermark).IsNotNull();
            var beyond = watermark!.Value + PastTailOffset;

            var answered = await transcript.ReadToEndAsync(
                new SessionLogRequest
                {
                    After = beyond,
                    Follow = QueryBoolean.False,
                },
                cancellationToken);

            await Assert.That(answered).Count().IsEqualTo(1);
            await Assert.That(answered[0]).IsTypeOf<EventLogSynced>();
            var marker = (EventLogSynced)answered[0];
            await Assert.That(marker.AggregateId).IsEqualTo(sessionId);
            await Assert.That(marker.Seq).IsNotNull();
            await Assert.That(marker.Seq!.Value).IsGreaterThanOrEqualTo(watermark.Value);
            await Assert.That(marker.Seq.Value).IsLessThan(beyond);

            Console.WriteLine(
                "session-log-past-tail-live: requested=" + Number(beyond) +
                " watermark=" + Number(marker.Seq.Value) +
                " replayed-before=" + Number(settled.Count) + " answered=" + Number(answered.Count));
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

    /// <summary>
    /// A cursor a few sequences past the tail suppresses exactly what it covers and no more. The
    /// following turn crosses it, so one attachment shows both halves: the execution start the
    /// cursor covers never arrives, while the terminal event beyond it does. A second reader on the
    /// correct cursor returns the skipped start, so the absence is a skip rather than a gap.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task GetLogAsync_Should_Skip_What_A_Cursor_Past_The_Tail_Covers_And_Resume_Beyond_It(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, "session-log-skipped", cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        var transcript = new SessionLogTranscript(session);
        cleanup.Own("session log enumerator", transcript.DisposeAsync);
        Exception? primaryFailure = null;

        try
        {
            var opening = new SimulatedSessionTurn(server, client, session, sessionId);
            var openingTerminal = await opening.CompleteAsync(SkippedPrompt, SkippedReply, cancellationToken);
            cleanup.MarkTurnCompleted();

            var settled = await transcript.ReadToEndAsync(
                new SessionLogRequest { Follow = QueryBoolean.False }, cancellationToken);
            await Assert.That(settled.OfType<SessionExecutionSucceeded>()
                .Any(succeeded => succeeded.Durable.Seq == openingTerminal.Durable.Seq)).IsTrue();
            var watermark = settled.OfType<EventLogSynced>().Single().Seq;
            await Assert.That(watermark).IsNotNull();
            var covered = watermark!.Value + SkippedOffset;

            await transcript.AttachAsync(covered, cancellationToken);
            await Assert.That(transcript.Marker).IsNotNull();
            await Assert.That(transcript.Marker!.Seq).IsNotNull();
            await Assert.That(transcript.Marker.Seq!.Value).IsGreaterThanOrEqualTo(watermark.Value);
            await Assert.That(transcript.Marker.Seq.Value).IsLessThan(covered);
            await Assert.That(transcript.ReplayItems).IsEmpty();

            cleanup.MarkTurnStarted();
            var followed = new SimulatedSessionTurn(server, client, session, sessionId);
            var terminal = await followed.CompleteAsync(SkippedFollowPrompt, SkippedFollowReply, cancellationToken);
            cleanup.MarkTurnCompleted();

            // Delivery resumed past the cursor: the attachment carried the turn to its terminal event.
            await transcript.ReadTurnAsync(sessionId, SkippedFollowReply);
            await Assert.That(transcript.Succeeded).IsNotNull();
            await Assert.That(transcript.Succeeded!.Durable.Seq).IsEqualTo(terminal.Durable.Seq);

            // The correct cursor returns an execution start inside the covered range on this same
            // session, so what the attachment never delivered was written and is readable.
            var control = await new SessionLogTranscript(session).ReadToEndAsync(
                new SessionLogRequest
                {
                    After = watermark.Value,
                    Follow = QueryBoolean.False,
                },
                cancellationToken);
            var skipped = control.OfType<SessionExecutionStarted>()
                .Where(started => started.Data.SessionId == sessionId && started.Durable.Seq <= covered)
                .ToList();
            await Assert.That(skipped).IsNotEmpty();
            await Assert.That(transcript.LiveItems.OfType<SessionExecutionStarted>()
                .Any(started => started.Durable.Seq == skipped[0].Durable.Seq)).IsFalse();

            Console.WriteLine(
                "session-log-skipped-live: cursor=" + Number(covered) +
                " watermark=" + Number(watermark.Value) +
                " skipped-start-seq=" + Number(skipped[0].Durable.Seq) +
                " resumed-terminal-seq=" + Number(transcript.Succeeded.Durable.Seq));
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
        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = title,
                Location = new LocationPublicRef { Directory = directory },
                Model = new ModelRef { Id = "sim-model", ProviderId = "sim" },
            },
            cancellationToken: cancellationToken);
        return created.Session.Id;
    }

    /// <summary>
    /// The durable union promises the identity and the envelope, so the lookup needs no arm per
    /// leaf type and cannot go quietly incomplete when the pin gains another durable event.
    /// </summary>
    private static int FindIndex(List<ISessionLogItem> items, string id, long sequence) =>
        items.FindIndex(item => item is ISessionEventDurable durable
                                && durable.Id == id
                                && durable.Durable?.Seq == sequence);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
