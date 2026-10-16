using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Sessions.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// The reversal triggers for what a session log answers on the profile an adopter actually
/// installs. A server started the way the distributed <c>opencode</c> CLI starts one runs with
/// event persistence off and offers no switch to turn it on. A replay then answers with the
/// <c>log.synced</c> marker alone however many durable events the session actually committed, and
/// a follow answers the same way: the server's live tail is a wake-up that re-reads the persisted
/// log, so with nothing persisted it delivers the marker and then nothing. Every other session-log
/// proof runs against the simulation host, which enables persistence deliberately, so nothing else
/// in the suite watches this profile.
/// </summary>
/// <remarks>
/// <b>When a test here fails, upstream changed what a default server answers.</b> That is a
/// documentation event, not a defect. The replay test fails when upstream starts persisting by
/// default; the follow test fails when a follow on such a server starts delivering live durable
/// items, whether because upstream persists by default or because its live tail stops depending on
/// persistence. Either way the streaming guide's account of the CLI-started server, the README's
/// known-issue relay, and the client-runtime architecture's persistence sentence for server-sent
/// events have to change with it, and the change belongs in the changelog because it widens what a
/// default server answers.
/// Replay observed marker-only on <c>@opencode/cli@2.0.2</c> and on the pinned source this fixture
/// runs; follow observed marker-only on <c>@opencode/cli@2.0.2</c>, <c>2.0.3</c>, and <c>2.0.15</c>
/// and on the pinned source.
/// The marker's <c>Seq</c> is asserted present, never positive: the aggregate watermark's starting
/// value differs across hosts and nothing upstream pins it.
/// </remarks>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionLogCliProfileLiveTests(PinnedOpenCodeServerFixture server)
{
    private const string RenamedTitle = "session-log-cli-profile renamed";
    private const string FollowedTitle = "session-log-cli-profile followed rename";

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long the follow must stay silent once the instance bus has carried the rename. Upstream
    /// wakes a follower in the same commit step that hands the event to the instance bus, so a
    /// server that delivered live items would have done so well inside this window. The bound only
    /// decides how soon a reversal is noticed: a slow host can make a delivery miss the window, but
    /// it cannot make this test fail.
    /// </summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(3);

    [Test]
    [Timeout(180_000)]
    public async Task GetLogAsync_Should_Replay_The_Marker_Alone_On_A_Cli_Profile_Server(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateSessionAsync(client, cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);

        // No model turn runs here, so there is never anything to interrupt.
        cleanup.MarkTurnCompleted();
        Exception? primaryFailure = null;

        try
        {
            // Two durable definitions committed: the session creation itself and one rename. A
            // persisting server would replay both before the marker.
            var renamed = await session.UpdateAsync(
                new SessionUpdateRequest { Title = RenamedTitle }, cancellationToken: cancellationToken);
            await Assert.That(renamed.Status).IsEqualTo(204);
            await Assert.That(renamed.IsError).IsFalse();

            var transcript = new SessionLogTranscript(session);
            var replay = await transcript.ReadToEndAsync(
                new SessionLogRequest { Follow = false },
                cancellationToken);

            await Assert.That(replay).Count().IsEqualTo(1);
            await Assert.That(replay[0]).IsTypeOf<EventLogSynced>();
            var marker = (EventLogSynced)replay[0];
            await Assert.That(marker.AggregateId).IsEqualTo(sessionId);
            await Assert.That(marker.Seq).IsNotNull();
            await Assert.That(replay.OfType<ISessionEventDurable>()).IsEmpty();

            Console.WriteLine(
                "session-log-cli-profile: mode=" + (server.IsExternal ? "external" : "owned") +
                " items=" + Number(replay.Count) +
                " durable=" + Number(replay.OfType<ISessionEventDurable>().Count()) +
                " marker=" + marker.Type +
                " seq=" + (marker.Seq?.ToString(CultureInfo.InvariantCulture) ?? "<null>"));
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
    public async Task GetLogAsync_Should_Follow_With_The_Marker_Alone_On_A_Cli_Profile_Server(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = await CreateSessionAsync(client, cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);

        // No model turn runs here, so there is never anything to interrupt.
        cleanup.MarkTurnCompleted();
        var reader = new OwnedEventReader(EventWait, CleanupTimeout, cancellationToken);
        var probe = new SessionEventProbe(reader);
        using var follow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        follow.CancelAfter(EventWait);
        var followed = new EventDiagnosticSummary();
        Exception? primaryFailure = null;

        try
        {
            // The instance bus is the positive control: it carries the rename whether or not the
            // follow does, so a silent follow reads as the server's answer rather than as a
            // mutation that never happened.
            probe.Start(client.Events.SubscribeAsync(cancellationToken: reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);

            await using var log = session.ReadLogAsync(
                    new SessionLogRequest { Follow = true },
                    cancellationToken: follow.Token)
                .GetAsyncEnumerator(follow.Token);
            var marker = await ReadMarkerAsync(log, followed, follow, cancellationToken);
            await Assert.That(marker.AggregateId).IsEqualTo(sessionId);
            await Assert.That(marker.Seq).IsNotNull();

            // The rename commits after the follow's attachment boundary, so it is exactly what a
            // live tail exists to deliver.
            var renamed = await session.UpdateAsync(
                new SessionUpdateRequest { Title = FollowedTitle }, cancellationToken: cancellationToken);
            await Assert.That(renamed.Status).IsEqualTo(204);
            await Assert.That(renamed.IsError).IsFalse();

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var published = await probe.WaitForAsync<SessionRenamed>(
                applied => applied.Data.SessionId == sessionId && applied.Data.Title == FollowedTitle,
                "session.renamed carrying the owned session's followed title", barrier.Token);

            // The quiet window starts once the control event is in hand. Anything the server sent
            // on the follow since the rename is already buffered on the open stream and is read here.
            follow.CancelAfter(QuietWindow);
            var live = await ReadLiveItemAsync(log, followed, follow, cancellationToken);
            await Assert.That(live?.Type).IsNull()
                .Because("a live item here means a CLI-profile follow now delivers; see the class remarks");

            Console.WriteLine(
                "session-log-cli-profile-follow: mode=" + (server.IsExternal ? "external" : "owned") +
                " marker=" + marker.Type +
                " seq=" + (marker.Seq?.ToString(CultureInfo.InvariantCulture) ?? "<null>") +
                " control=" + published.Type +
                " quiet-ms=" + Number((int)QuietWindow.TotalMilliseconds) +
                " followed=" + followed);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (!exception.Data.Contains(EventDiagnosticSummary.DataKey))
            {
                exception.Data[EventDiagnosticSummary.DataKey] =
                    "follow: " + followed + "; bus: " + probe.DiagnosticSummary;
            }
        }
        finally
        {
            await CompleteCleanupAsync(reader, cleanup, primaryFailure);
        }
    }

    private static async Task<string> CreateSessionAsync(
        OpenCodeClient client,
        CancellationToken cancellationToken)
    {
        var resolved = (await client.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation;
        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = "session-log-cli-profile",
                Location = new LocationPublicRef
                {
                    Directory = resolved.Directory,
                },
            },
            cancellationToken: cancellationToken);
        return created.Session.Id;
    }

    private static async Task CompleteCleanupAsync(
        OwnedEventReader reader,
        OwnedSessionCleanup cleanup,
        Exception? primaryFailure)
    {
        try
        {
            await reader.CompleteAsync(primaryFailure);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    /// <summary>
    /// Reads the follow up to its attachment marker. Replayed items before it are recorded but not
    /// judged here; the replay test owns that half.
    /// </summary>
    private static async Task<EventLogSynced> ReadMarkerAsync(
        IAsyncEnumerator<ISessionLogItem> log,
        EventDiagnosticSummary followed,
        CancellationTokenSource window,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await log.MoveNextAsync())
            {
                followed.Add(log.Current.Type);
                if (log.Current is EventLogSynced marker)
                {
                    return marker;
                }
            }
        }
        catch (OperationCanceledException exception)
            when (window.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The followed log sent no log.synced marker before the wait ended. Items: " + followed + ".",
                exception);
        }

        throw new InvalidOperationException(
            "The followed log ended before its log.synced marker. Items: " + followed + ".");
    }

    /// <summary>
    /// The first item the follow delivers after its marker, or <see langword="null"/> when the
    /// window closes with nothing delivered and the stream still open. A stream the server ends
    /// instead is a different answer and fails as one.
    /// </summary>
    private static async Task<ISessionLogItem?> ReadLiveItemAsync(
        IAsyncEnumerator<ISessionLogItem> log,
        EventDiagnosticSummary followed,
        CancellationTokenSource window,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await log.MoveNextAsync())
            {
                followed.Add(log.Current.Type);
                return log.Current;
            }
        }
        catch (OperationCanceledException)
            when (window.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        throw new InvalidOperationException(
            "The followed log ended after its marker instead of staying open. Items: " + followed + ".");
    }

    /// <summary>Renders one number for the console line, culture-free.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
