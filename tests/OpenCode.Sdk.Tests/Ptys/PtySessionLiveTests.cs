using System.Globalization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Ptys;

/// <summary>
/// Proves the normal PTY family's HTTP lifecycle and live WebSocket replay contract against the
/// accepted server pin. A repository-owned Bun peer acknowledges only executed input, so terminal
/// echo cannot satisfy the assertion.
/// </summary>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PtySessionLiveTests(PinnedOpenCodeServerFixture server)
{
    private const string Command = "bun";

    private const string InitialTitle = "sdk normal pty live";

    private const string ReadyRecord = "READY";

    /// <summary>The line the peer answers with <see cref="ReadyRecord"/> once it owns stdin.</summary>
    private const string ReadyRequest = "READY?";

    /// <summary>The peer's execution request; only a line its reader completed produces an answer.</summary>
    private const string RunPrefix = "RUN ";

    /// <summary>The peer's answer to an executed request, carrying the nonce it was given.</summary>
    private const string AcknowledgementPrefix = "ACK:";

    /// <summary>The title of the terminal the submit door drives; the lifecycle test owns its own.</summary>
    private const string SubmitTitle = "sdk normal pty submit";

    private const string UpdatedTitle = "sdk normal pty live updated";

    private readonly FixtureLoader _fixtures = new();
    private readonly PtyLiveScenario _scenario = new(server);

    [Test]
    [Timeout(120_000)]
    public async Task Pty_Should_Create_Execute_Replay_Resume_And_Remove(CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        try
        {
            await _scenario.InitializeAsync(cancellationToken);
            var http = await AssertHttpLifecycleAsync(cancellationToken);
            var socket = await AssertSocketLifecycleAsync(http.Terminal, http.PtyId, cancellationToken);

            Console.WriteLine(
                "pty-live: mode=" + _scenario.Mode +
                " create=" + Number(http.CreateStatus) +
                " id=" + http.PtyId +
                " pid=" + Number(http.Pid) +
                " token-http=" + Number(http.TokenStatus) +
                " socket=basic" +
                " replay-cursor=" + Number(socket.ReplayCursor) +
                " resume-cursor=" + Number(socket.ResumeCursor) +
                " ack-a=" + socket.AcknowledgementA +
                " ack-b=" + socket.AcknowledgementB +
                " remove=" + Number(socket.RemoveStatus) +
                " get-after=" + Number(socket.GetAfterStatus) +
                " remove-after=" + Number(socket.RemoveAfterStatus) +
                " listed-after=" + Number(socket.ListedAfter));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await _scenario.CleanupAsync(primaryFailure);
    }

    /// <summary>
    /// The submit door against a real terminal: the caller hands over a bare line and the peer
    /// answers only if the line was submitted. The peer acknowledges executed input alone, so an
    /// answer proves the terminator the SDK added reached the line discipline - on every OS this
    /// suite runs, including the Windows console host whose Enter is a carriage return.
    /// </summary>
    /// <param name="cancellationToken">The test's own deadline.</param>
    /// <returns>The running test.</returns>
    [Test]
    [Timeout(120_000)]
    public async Task SubmitAsync_Should_Execute_A_Line_It_Terminates_Itself(CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        try
        {
            await _scenario.InitializeAsync(cancellationToken);
            var nonce = await SubmitThroughTheTerminalAsync(cancellationToken);

            Console.WriteLine("pty-live: submit mode=" + _scenario.Mode + " executed=" + nonce);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await _scenario.CleanupAsync(primaryFailure);
    }

    /// <summary>
    /// Creates the peer terminal, submits its readiness request and one execution request, and
    /// returns the nonce the peer acknowledged.
    /// </summary>
    /// <param name="cancellationToken">The test's own deadline.</param>
    /// <returns>The nonce the peer acknowledged.</returns>
    private async Task<string> SubmitThroughTheTerminalAsync(CancellationToken cancellationToken)
    {
        _scenario.Diagnostics.Enter("submit create");
        var created = await _scenario.Client.Ptys.CreatePtyAsync(
            new PtyCreateRequest
            {
                Command = Command,
                Args = ["-e", _fixtures.LoadText("Ptys.terminal-peer.js")],
                Cwd = _scenario.Directory,
                Title = SubmitTitle,
                Location = _scenario.Location,
            },
            cancellationToken: cancellationToken);
        _scenario.Diagnostics.Created(created.Pty.Id, created.Pty.Pid, created.Status);
        var terminal = _scenario.Client.Ptys.GetPtyClient(created.Pty.Id);
        _scenario.Own(terminal);
        await Assert.That(created.Status).IsEqualTo(200);

        _scenario.Diagnostics.Enter("submit connect");
        var session = _scenario.Own(await terminal.ConnectAsync(
            new PtyConnectOptions { Location = _scenario.Location }, cancellationToken));
        var transcript = new PtyLiveTranscript();
        _scenario.Diagnostics.Enter("submitted readiness", transcript);
        await session.SubmitAsync(ReadyRequest, cancellationToken);
        _ = await transcript.ReadThroughReplayAsync(session, [ReadyRecord], cancellationToken);

        // Neither submit is handed a terminator: the peer's reader completes a line only when the
        // line discipline saw one, so an acknowledgement is the SDK's own carriage return observed.
        var nonce = Guid.NewGuid().ToString("N");
        _scenario.Diagnostics.Enter("submitted execution", transcript);
        await session.SubmitAsync(RunPrefix + nonce, cancellationToken);
        await transcript.ReadUntilRecordAsync(session, AcknowledgementPrefix + nonce, cancellationToken);
        await Assert.That(transcript.ContainsRecord(AcknowledgementPrefix + nonce)).IsTrue();
        await session.DisposeAsync();
        return nonce;
    }

    private async Task<PtyHttpEvidence> AssertHttpLifecycleAsync(CancellationToken cancellationToken)
    {
        _scenario.Diagnostics.Enter("create");
        var created = await _scenario.Client.Ptys.CreatePtyAsync(
            new PtyCreateRequest
            {
                Command = Command,
                Args = ["-e", _fixtures.LoadText("Ptys.terminal-peer.js")],
                Cwd = _scenario.Directory,
                Title = InitialTitle,
                Location = _scenario.Location,
            },
            cancellationToken: cancellationToken);

        var ptyId = created.Pty.Id;
        _scenario.Diagnostics.Created(ptyId, created.Pty.Pid, created.Status);
        _scenario.Diagnostics.Enter("HTTP lifecycle");
        var terminal = _scenario.Client.Ptys.GetPtyClient(ptyId);
        _scenario.Own(terminal);

        await Assert.That(created.Status).IsEqualTo(200);
        await Assert.That(created.IsError).IsFalse();
        await Assert.That(created.Pty.Status).IsEqualTo(PtyStatus.Running);
        await Assert.That(created.Pty.Id.Length).IsGreaterThan(0);
        await Assert.That(created.Pty.Pid).IsGreaterThan(0);
        await Assert.That(created.Pty.Command).IsEqualTo(Command);
        await Assert.That(created.Pty.Cwd).IsEqualTo(_scenario.Directory);

        var listed = await _scenario.Client.Ptys.ListPtysAsync(
            new PtyListRequest { Location = _scenario.Location },
            cancellationToken: cancellationToken);
        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.Ptys.Select(static pty => pty.Id).ToArray()).Contains(ptyId);

        var fetched = await terminal.GetAsync(
            new PtyRequest { Location = _scenario.Location },
            cancellationToken: cancellationToken);
        await Assert.That(fetched.Status).IsEqualTo(200);
        await Assert.That(fetched.Pty.Id).IsEqualTo(ptyId);

        var updated = await terminal.UpdateAsync(
            new PtyUpdateRequest { Title = UpdatedTitle, Location = _scenario.Location },
            cancellationToken: cancellationToken);
        await Assert.That(updated.Status).IsEqualTo(200);
        await Assert.That(updated.Pty.Title).IsEqualTo(UpdatedTitle);

        var fetchedAfterUpdate = await terminal.GetAsync(
            new PtyRequest { Location = _scenario.Location },
            cancellationToken: cancellationToken);
        await Assert.That(fetchedAfterUpdate.Pty.Id).IsEqualTo(ptyId);
        await Assert.That(fetchedAfterUpdate.Pty.Title).IsEqualTo(UpdatedTitle);

        var token = await terminal.CreateConnectTokenAsync(
            new PtyConnectTokenRequest { Location = _scenario.Location },
            cancellationToken: cancellationToken);
        await Assert.That(token.Status).IsEqualTo(200);
        await Assert.That(token.IsError).IsFalse();
        await Assert.That(token.ConnectToken.Ticket.Length).IsGreaterThan(0);
        await Assert.That(token.ConnectToken.ExpiresIn).IsGreaterThan(0);

        return new PtyHttpEvidence
        {
            Terminal = terminal,
            PtyId = ptyId,
            Pid = created.Pty.Pid,
            CreateStatus = created.Status,
            TokenStatus = token.Status,
        };
    }

    private async Task<PtySocketEvidence> AssertSocketLifecycleAsync(
        PtyClient terminal,
        string ptyId,
        CancellationToken cancellationToken)
    {
        var acknowledgementA = await AssertInitialExecutionAsync(terminal, cancellationToken);
        var replay = await AssertReplayAndResumeAsync(terminal, acknowledgementA, cancellationToken);
        var removal = await AssertRemovalAsync(terminal, ptyId, replay, cancellationToken);

        return new PtySocketEvidence
        {
            AcknowledgementA = acknowledgementA,
            AcknowledgementB = replay.AcknowledgementB,
            ReplayCursor = replay.ReplayCursor,
            ResumeCursor = replay.ResumeCursor,
            RemoveStatus = removal.RemoveStatus,
            GetAfterStatus = removal.GetAfterStatus,
            RemoveAfterStatus = removal.RemoveAfterStatus,
            ListedAfter = removal.ListedAfter,
        };
    }

    private async Task<string> AssertInitialExecutionAsync(
        PtyClient terminal,
        CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var acknowledgement = AcknowledgementPrefix + nonce;
        _scenario.Diagnostics.Enter("initial connect");
        var session = _scenario.Own(await terminal.ConnectAsync(
            new PtyConnectOptions { Location = _scenario.Location }, cancellationToken));
        var transcript = new PtyLiveTranscript();
        _scenario.Diagnostics.Enter("readiness request", transcript);
        await session.WriteAsync(ReadyRequest + "\r", cancellationToken);
        _scenario.Diagnostics.Enter("initial READY", transcript);
        _ = await transcript.ReadThroughReplayAsync(session, [ReadyRecord], cancellationToken);
        _scenario.Diagnostics.Enter("initial input acknowledgement", transcript);
        await session.WriteAsync(RunPrefix + nonce + "\r", cancellationToken);
        await transcript.ReadUntilRecordAsync(session, acknowledgement, cancellationToken);
        await AssertReadCancellationReuseAsync(session, cancellationToken);
        await session.DisposeAsync();
        return acknowledgement;
    }

    private async Task AssertReadCancellationReuseAsync(PtySession session, CancellationToken cancellationToken)
    {
        _scenario.Diagnostics.Enter("cancel read");
        var canceled = false;
        while (!canceled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stopReading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var reader = session.ReadAsync(stopReading.Token).GetAsyncEnumerator(stopReading.Token);
            while (true)
            {
                var move = reader.MoveNextAsync();
                var pending = move.AsTask();
                if (pending.IsCompleted)
                {
                    await Assert.That(await pending).IsTrue();
                    continue;
                }

                // Drain any trailing terminal output first. Cancellation is exercised only
                // after a move actually suspends, never on an already-selected frame.
                await stopReading.CancelOnWorkerAsync();
                try
                {
                    await Assert.That(await pending).IsTrue();
                }
                catch (OperationCanceledException) when (stopReading.IsCancellationRequested)
                {
                    canceled = true;
                }

                // Output selected between the incomplete-task observation and cancellation
                // may win. Retry with a fresh token; only cancellation of this pending move
                // supplies the proof, never the next move on an already-canceled token.
                break;
            }
        }

        var nonce = Guid.NewGuid().ToString("N");
        var transcript = new PtyLiveTranscript();
        _scenario.Diagnostics.Enter("execute after read cancellation", transcript);
        await session.WriteAsync(RunPrefix + nonce + "\r", cancellationToken);
        await transcript.ReadUntilRecordAsync(session, AcknowledgementPrefix + nonce, cancellationToken);
        Console.WriteLine("pty-live: read-canceled same-connection-execution=" + nonce);
    }

    private async Task<PtyReplayEvidence> AssertReplayAndResumeAsync(
        PtyClient terminal,
        string acknowledgementA,
        CancellationToken cancellationToken)
    {
        _scenario.Diagnostics.Enter("replay connect");
        var replay = _scenario.Own(await terminal.ConnectAsync(
            new PtyConnectOptions { Location = _scenario.Location }, cancellationToken));
        var replayTranscript = new PtyLiveTranscript();
        _scenario.Diagnostics.Enter("replay records", replayTranscript);
        var replayCursor = await replayTranscript.ReadThroughReplayAsync(replay, [], cancellationToken);
        await Assert.That(replayTranscript.ContainsRecord(ReadyRecord)).IsTrue();
        await Assert.That(replayTranscript.ContainsRecord(acknowledgementA)).IsTrue();

        var nonceB = Guid.NewGuid().ToString("N");
        var acknowledgementB = AcknowledgementPrefix + nonceB;
        _scenario.Diagnostics.Enter("replay input acknowledgement", replayTranscript);
        await replay.WriteAsync(RunPrefix + nonceB + "\r", cancellationToken);
        await replayTranscript.ReadUntilRecordAsync(replay, acknowledgementB, cancellationToken);
        await replay.DisposeAsync();

        _scenario.Diagnostics.Enter("resume connect");
        var resumed = _scenario.Own(await terminal.ConnectAsync(
            new PtyConnectOptions { Location = _scenario.Location, Cursor = replayCursor }, cancellationToken));
        var resumedTranscript = new PtyLiveTranscript();
        _scenario.Diagnostics.Enter("resume records", resumedTranscript);
        var resumeCursor = await resumedTranscript.ReadThroughReplayAsync(resumed, [], cancellationToken);
        await Assert.That(resumedTranscript.ContainsRecord(acknowledgementB)).IsTrue();
        await Assert.That(resumedTranscript.ContainsRecord(ReadyRecord)).IsFalse();
        await Assert.That(resumedTranscript.ContainsRecord(acknowledgementA)).IsFalse();

        return new PtyReplayEvidence
        {
            Session = resumed,
            Transcript = resumedTranscript,
            AcknowledgementB = acknowledgementB,
            ReplayCursor = replayCursor,
            ResumeCursor = resumeCursor,
        };
    }

    private async Task<PtyRemovalEvidence> AssertRemovalAsync(
        PtyClient terminal,
        string ptyId,
        PtyReplayEvidence replay,
        CancellationToken cancellationToken)
    {
        _scenario.Diagnostics.Enter("removal and socket close", replay.Transcript);
        var completion = replay.Transcript.ReadToCompletionAsync(replay.Session, cancellationToken);
        _scenario.Observe(completion);
        var removed = await terminal.RemoveAsync(
            new PtyRemoveRequest { Location = _scenario.Location },
            cancellationToken: cancellationToken);
        if (removed.Status is 204)
        {
            _scenario.MarkRemoved();
        }

        await Assert.That(removed.Status).IsEqualTo(204);
        await completion;

        var missingGet = await terminal.GetAsync(
            new PtyRequest { Location = _scenario.Location },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);
        await AssertNotFoundAsync(missingGet, ptyId);

        var missingRemove = await terminal.RemoveAsync(
            new PtyRemoveRequest { Location = _scenario.Location },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);
        await AssertNotFoundAsync(missingRemove, ptyId);

        var afterRemoval = await _scenario.Client.Ptys.ListPtysAsync(
            new PtyListRequest { Location = _scenario.Location },
            cancellationToken: cancellationToken);
        await Assert.That(afterRemoval.Ptys.Select(static pty => pty.Id).ToArray()).DoesNotContain(ptyId);

        return new PtyRemovalEvidence
        {
            RemoveStatus = removed.Status,
            GetAfterStatus = missingGet.Status,
            RemoveAfterStatus = missingRemove.Status,
            ListedAfter = afterRemoval.Ptys.Count,
        };
    }

    private static async Task AssertNotFoundAsync(OpenCodeResponse response, string ptyId)
    {
        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Error).IsTypeOf<PtyNotFoundError>();
        var error = response.Error as PtyNotFoundError;
        await Assert.That(error?.PtyId).IsEqualTo(ptyId);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record PtyHttpEvidence
    {
        public required PtyClient Terminal { get; init; }

        public required string PtyId { get; init; }

        public required long Pid { get; init; }

        public required int CreateStatus { get; init; }

        public required int TokenStatus { get; init; }
    }

    private sealed record PtyReplayEvidence
    {
        public required PtySession Session { get; init; }

        public required PtyLiveTranscript Transcript { get; init; }

        public required string AcknowledgementB { get; init; }

        public required long ReplayCursor { get; init; }

        public required long ResumeCursor { get; init; }
    }

    private sealed record PtyRemovalEvidence
    {
        public required int RemoveStatus { get; init; }

        public required int GetAfterStatus { get; init; }

        public required int RemoveAfterStatus { get; init; }

        public required int ListedAfter { get; init; }
    }

    private sealed record PtySocketEvidence
    {
        public required string AcknowledgementA { get; init; }

        public required string AcknowledgementB { get; init; }

        public required long ReplayCursor { get; init; }

        public required long ResumeCursor { get; init; }

        public required int RemoveStatus { get; init; }

        public required int GetAfterStatus { get; init; }

        public required int RemoveAfterStatus { get; init; }

        public required int ListedAfter { get; init; }
    }
}
