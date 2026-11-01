using System.Globalization;
using System.Text;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.PersistentPtys;

/// <summary>
/// The persistent PTY family's live proof against the pinned server (ADR-0021, ADR-0022): one
/// terminal driven end to end where the <c>opencode-pty</c> daemon exists, and the family's
/// daemon-absent answers where it does not. Both arms assert real server answers and neither is a
/// skip - the daemon ships for darwin, linux and win32, so every leg proves the round trip by
/// default. <see cref="PersistentPtyDaemonGate.DaemonExpected"/> selects
/// the arm, and the arm that ran names itself on the console.
/// </summary>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PersistentPtyLiveTests(PinnedOpenCodeServerFixture server)
{
    /// <summary>The framed input protocol this SDK speaks; the server negotiates it at attach.</summary>
    private const int FramedInputProtocol = 1;

    /// <summary>The columns the live resize asks for; the create default is 80.</summary>
    private const int ResizedCols = 100;

    /// <summary>The rows the live resize asks for; the create default is 24.</summary>
    private const int ResizedRows = 30;

    /// <summary>
    /// How many back-to-back screen reads the round trip makes, before the resize, for the server's
    /// screen to show the echoed marker the stream already carried. An attempt count, not a timer, so
    /// the read result decides and the test fails on its screen assertion rather than a timeout.
    /// </summary>
    private const int ScreenReadAttempts = 200;

    private const string SessionTitle = "persistent pty live";

    private const string TerminalTitle = "sdk persistent pty live";

    /// <summary>
    /// The terminating line feed is Enter here: the daemon's terminals run a Unix line
    /// discipline, so LF submits the line. The normal PTY family's carriage-return finding was
    /// PSReadLine on a Windows console host, which this family never reaches.
    /// </summary>
    private const string EchoCommand = "echo sdk-live\n";

    /// <summary>What the terminal echoes back, then prints again as the command's own output.</summary>
    private const string EchoMarker = "sdk-live";

    /// <summary>
    /// The line the submit door is handed with no terminator of its own. The empty quotes split
    /// the marker inside the typed text, so the terminal's echo of this line cannot carry
    /// <see cref="SubmitMarker"/> - only the shell's own output can, which is what makes a single
    /// occurrence proof that the line was submitted rather than merely typed.
    /// </summary>
    private const string SubmittedLine = "echo sdk''-submitted";

    /// <summary>The text only an executed line prints.</summary>
    private const string SubmitMarker = "sdk-submitted";

    /// <summary>The service the daemon-absent 503 names; the one value that tells it from any other 503.</summary>
    private const string DaemonService = "opencode-pty";

    [Test]
    [Timeout(120_000)]
    public async Task Terminal_Lifecycle_Should_Round_Trip_Or_Answer_The_Daemon_Absent_Arm(
        CancellationToken cancellationToken) =>
        await WithTerminalAsync(AssertRoundTripAsync, cancellationToken);

    /// <summary>
    /// The submit door against the daemon's own terminal: one line handed over without a
    /// terminator. Where the daemon exists the shell runs it; where it does not, <c>create</c>
    /// answers the declared 503 and there is no terminal to submit on. Both arms assert.
    /// </summary>
    /// <param name="cancellationToken">The test's own deadline.</param>
    /// <returns>The running test.</returns>
    [Test]
    [Timeout(120_000)]
    public async Task SubmitAsync_Should_Execute_A_Line_Or_Answer_The_Daemon_Absent_Arm(
        CancellationToken cancellationToken) =>
        await WithTerminalAsync(
            static (client, _, created, token) => AssertSubmittedLineRunsAsync(client, created, token),
            cancellationToken, AssertSubmitHasNoTerminalAsync);

    [Test]
    [Timeout(120_000)]
    public async Task SessionRemoval_Should_Remove_Its_Terminal_Or_Answer_The_Daemon_Absent_Arm(
        CancellationToken cancellationToken) =>
        await WithTerminalAsync(AssertSessionRemovalAsync, cancellationToken);

    /// <summary>Creates the shared session and terminal, then asserts the selected daemon arm.</summary>
    private async Task WithTerminalAsync(
        Func<OpenCodeClient, string, PersistentPtyCreateResponse, CancellationToken, Task> assertPresent,
        CancellationToken cancellationToken,
        Func<PersistentPtyCreateResponse, Task>? assertAbsent = null)
    {
        // No workspace: the terminals this family creates live on the server's own machine, so a
        // Windows path would be meaningless to the Linux server the WSL2 recipe points this test
        // at, and that session lands in the external server's own working directory. An owned
        // server's session takes the fixture's own default location instead.
        using var client = server.CreateClient();
        var session = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest { Title = SessionTitle, Location = server.DefaultSessionLocation },
            cancellationToken: cancellationToken);
        var created = await client.PersistentPtys.CreatePersistentPtyAsync(
            session.Session.Id, CreateRequest(), OpenCodeRequestOptions.NoThrow, cancellationToken);

        if (!PersistentPtyDaemonGate.DaemonExpected)
        {
            if (assertAbsent is not null)
            {
                await assertAbsent(created);
            }
            else
            {
                await AssertDaemonAbsentArmsAsync(client, session.Session.Id, created, cancellationToken);
            }

            return;
        }

        await assertPresent(client, session.Session.Id, created, cancellationToken);
    }

    /// <summary>Observes the asynchronous terminal cleanup queued by removing its session.</summary>
    private static async Task AssertSessionRemovalAsync(
        OpenCodeClient client,
        string sessionId,
        PersistentPtyCreateResponse created,
        CancellationToken cancellationToken)
    {
        await Assert.That(created.IsError).IsFalse();
        var terminal = client.PersistentPtys.GetPersistentPtyClient(created.PersistentPty.Id);
        var observed = await terminal.GetAsync(cancellationToken: cancellationToken);
        await Assert.That(observed.PersistentPty.Id).IsEqualTo(created.PersistentPty.Id);

        _ = await client.Sessions.GetSessionClient(sessionId).RemoveAsync(cancellationToken: cancellationToken);

        observed = await LiveReadiness.WaitAsync(
            ct => terminal.GetAsync(OpenCodeRequestOptions.NoThrow, ct),
            static response => response.Status != 200,
            "the session's terminal to be removed", cancellationToken);

        await Assert.That(observed.Status).IsEqualTo(404);
        await Assert.That(observed.Error).IsTypeOf<PtyNotFoundError>();
        await Assert.That(((PtyNotFoundError)observed.Error!).PtyId).IsEqualTo(created.PersistentPty.Id);
        Console.WriteLine("ppty-live: arm=round-trip session-removed=" + sessionId +
            " terminal=" + created.PersistentPty.Id + " get=" + Number(observed.Status));
    }

    /// <summary>
    /// The arm where the daemon does not exist. <c>create</c> is the route that would have started
    /// it, so its declared 503 is exactly why no submit can reach a terminal here - the branch
    /// asserts that answer rather than skipping the test.
    /// </summary>
    /// <param name="created">The create answer this platform produced.</param>
    /// <returns>The assertion.</returns>
    private static async Task AssertSubmitHasNoTerminalAsync(PersistentPtyCreateResponse created)
    {
        await Assert.That(created.Status).IsEqualTo(503);
        await Assert.That(created.IsError).IsTrue();
        await Assert.That(created.Error).IsTypeOf<ServiceUnavailableError>();
        var unavailable = created.Error as ServiceUnavailableError;
        await Assert.That(unavailable?.Service).IsEqualTo(DaemonService);

        Console.WriteLine(
            "ppty-live: arm=daemon-absent submit=no-terminal create=" + Number(created.Status) +
            " service=" + unavailable?.Service);
    }

    /// <summary>
    /// The arm where the daemon exists: the submitted line runs, and the marker its output prints
    /// cannot have come from the terminal's echo of the typed text.
    /// </summary>
    /// <param name="client">The client the test owns.</param>
    /// <param name="created">The terminal the daemon created.</param>
    /// <param name="cancellationToken">The test's own deadline.</param>
    /// <returns>The assertion.</returns>
    private static async Task AssertSubmittedLineRunsAsync(
        OpenCodeClient client,
        PersistentPtyCreateResponse created,
        CancellationToken cancellationToken)
    {
        await Assert.That(created.Status).IsEqualTo(200);
        await Assert.That(created.IsError).IsFalse();

        var terminal = client.PersistentPtys.GetPersistentPtyClient(created.PersistentPty.Id);
        var output = await SubmitOneLineAsync(terminal, cancellationToken);
        await Assert.That(output).Contains(SubmitMarker);

        var removed = await terminal.RemoveAsync(cancellationToken: cancellationToken);
        await Assert.That(removed.Status).IsEqualTo(204);

        Console.WriteLine(
            "ppty-live: arm=round-trip id=" + created.PersistentPty.Id +
            " submitted=" + Observed(output, SubmitMarker) +
            " remove=" + Number(removed.Status));
    }

    /// <summary>
    /// Attaches, submits one line, and reads until the executed output arrives. The session is
    /// disposed before the caller removes the terminal, so the removal is not racing a live socket.
    /// </summary>
    /// <param name="terminal">The terminal handle to attach to.</param>
    /// <param name="cancellationToken">The test's own deadline.</param>
    /// <returns>The output the terminal produced, decoded together.</returns>
    private static async Task<string> SubmitOneLineAsync(
        PersistentPtyClient terminal, CancellationToken cancellationToken)
    {
        await using var session = await terminal.ConnectAsync(cancellationToken: cancellationToken);

        await session.SubmitAsync(SubmittedLine, cancellationToken);
        return await ReadUntilMarkerAsync(session, SubmitMarker, cancellationToken);
    }

    /// <summary>
    /// The create body the live arms share: no command, so the server resolves its own default
    /// shell, and no size, so the create defaults (80x24) are what the later resize changes.
    /// </summary>
    private static PersistentPtyCreateRequest CreateRequest() => new()
    {
        Args = [],
        Title = TerminalTitle,
        Env = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    /// <summary>
    /// The arms every route takes where the daemon does not exist. <c>create</c> is the only route
    /// that starts the daemon, so it is the only one that fails; the rest answer their
    /// existence-independent arms, which is what makes this branch assert rather than skip.
    /// </summary>
    private static async Task AssertDaemonAbsentArmsAsync(
        OpenCodeClient client,
        string sessionId,
        PersistentPtyCreateResponse created,
        CancellationToken cancellationToken)
    {
        await Assert.That(created.Status).IsEqualTo(503);
        await Assert.That(created.IsError).IsTrue();
        await Assert.That(created.Error).IsTypeOf<ServiceUnavailableError>();
        var unavailable = created.Error as ServiceUnavailableError;
        await Assert.That(unavailable?.Service).IsEqualTo(DaemonService);

        var listed = await client.PersistentPtys.ListPersistentPtysAsync(
            sessionId, cancellationToken: cancellationToken);
        await Assert.That(listed.PersistentPtys.Count).IsEqualTo(0);

        var read = await client.PersistentPtys.ReadAsync(sessionId, cancellationToken: cancellationToken);
        await Assert.That(read.IsError).IsFalse();
        await Assert.That(read.Read).IsNull();

        var handoff = await client.PersistentPtys.HandoffAsync(cancellationToken: cancellationToken);
        await Assert.That(handoff.IsError).IsFalse();
        await Assert.That(handoff.Handoff).IsNull();

        var shutdown = await client.PersistentPtys.ShutdownAsync(cancellationToken: cancellationToken);
        await Assert.That(shutdown.Status).IsEqualTo(204);

        // Every value on this line is what the server answered, never a constant restating what
        // the assertions above already required: evidence a reader can disbelieve is not evidence.
        Console.WriteLine(
            "ppty-live: arm=daemon-absent create=" + Number(created.Status) +
            " service=" + unavailable?.Service +
            " list=" + Number(listed.PersistentPtys.Count) +
            " read=" + Describe(read.Read?.PtyId) +
            " handoff=" + Describe(handoff.Handoff?.InstanceId) +
            " shutdown=" + Number(shutdown.Status));
    }

    /// <summary>
    /// The full round trip where the daemon exists: the terminal is created, listed, snapshotted,
    /// driven over its live socket, read back over HTTP, and removed.
    /// </summary>
    private static async Task AssertRoundTripAsync(
        OpenCodeClient client,
        string sessionId,
        PersistentPtyCreateResponse created,
        CancellationToken cancellationToken)
    {
        await Assert.That(created.Status).IsEqualTo(200);
        await Assert.That(created.IsError).IsFalse();
        await Assert.That(created.PersistentPty.Status).IsEqualTo(PersistentPtyInfoStatus.Running);
        await Assert.That(created.PersistentPty.SessionId).IsEqualTo(sessionId);

        var ptyId = created.PersistentPty.Id;
        var listed = await client.PersistentPtys.ListPersistentPtysAsync(
            sessionId, cancellationToken: cancellationToken);
        await Assert.That(listed.PersistentPtys.Select(static pty => pty.Id).ToArray()).Contains(ptyId);

        var terminal = client.PersistentPtys.GetPersistentPtyClient(ptyId);
        var renamed = await terminal.UpdateAsync(
            new PersistentPtyUpdateRequest { Title = "build" }, cancellationToken: cancellationToken);
        await Assert.That(renamed.PersistentPty.UserTitle).IsEqualTo("build");
        await Assert.That(renamed.PersistentPty.Title).IsEqualTo("build");
        var reread = await terminal.GetAsync(cancellationToken: cancellationToken);
        await Assert.That(reread.PersistentPty.UserTitle).IsEqualTo("build");
        var cleared = await terminal.UpdateAsync(
            new PersistentPtyUpdateRequest { Title = null }, cancellationToken: cancellationToken);
        await Assert.That(cleared.PersistentPty.UserTitle).IsNull();
        await Assert.That(cleared.PersistentPty.Title).IsNotEqualTo("build");
        var afterClear = await terminal.GetAsync(cancellationToken: cancellationToken);
        await Assert.That(afterClear.PersistentPty.UserTitle).IsNull();

        var snapshot = await terminal.GetSnapshotAsync(cancellationToken: cancellationToken);
        await Assert.That(snapshot.Snapshot.Info.Id).IsEqualTo(ptyId);

        var drive = await DriveTerminalAsync(client, terminal, sessionId, cancellationToken);

        var removed = await terminal.RemoveAsync(cancellationToken: cancellationToken);
        await Assert.That(removed.Status).IsEqualTo(204);

        var remaining = await client.PersistentPtys.ListPersistentPtysAsync(
            sessionId, cancellationToken: cancellationToken);
        await Assert.That(remaining.PersistentPtys.Select(static pty => pty.Id).ToArray()).DoesNotContain(ptyId);

        // Every value is observed: the echo and read excerpts are what the terminal and the read
        // route actually returned, so the line stays evidence rather than a restatement of the
        // marker the assertions already matched.
        Console.WriteLine(
            "ppty-live: arm=round-trip id=" + ptyId +
            " status=" + created.PersistentPty.Status +
            " renamed=" + renamed.PersistentPty.Title +
            " role=" + drive.Role +
            " inputProtocol=" + Number(drive.InputProtocol) +
            " echo=" + Observed(drive.Echoed, EchoMarker) +
            " resize=" + Number(drive.Cols) + "x" + Number(drive.Rows) +
            " read=" + Observed(drive.Screen, EchoMarker) +
            " remove=" + Number(removed.Status) +
            " listed-after=" + Describe(remaining.PersistentPtys.FirstOrDefault(pty => pty.Id == ptyId)?.Id));
    }

    /// <summary>
    /// Drives the terminal over its live socket: attach, echo, resize, and the HTTP read the
    /// controller attach selected this terminal for. Every wait is bounded by the test's own
    /// token, and the screen read also by <see cref="ScreenReadAttempts"/>.
    /// </summary>
    private static async Task<TerminalDrive> DriveTerminalAsync(
        OpenCodeClient client,
        PersistentPtyClient terminal,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var session = await terminal.ConnectAsync(cancellationToken: cancellationToken);

        var attachment = session.Attachment;
        await Assert.That(attachment.Role).IsEqualTo(PersistentPtyRole.Controller);
        await Assert.That(attachment.InputProtocol).IsEqualTo(FramedInputProtocol);

        await session.WriteAsync(Encoding.UTF8.GetBytes(EchoCommand), cancellationToken);
        var echoed = await ReadUntilMarkerAsync(session, EchoMarker, cancellationToken);
        await Assert.That(echoed).Contains(EchoMarker);

        // The read route answers from the session's most recently controlled terminal, and this
        // connection's controller attach is what selected it - so a payload here is the selection
        // observed, not merely the terminal's existence.
        var screen = string.Empty;
        for (var attempt = 0; attempt < ScreenReadAttempts && !screen.Contains(EchoMarker, StringComparison.Ordinal); attempt++)
        {
            var read = await client.PersistentPtys.ReadAsync(sessionId, cancellationToken: cancellationToken);
            await Assert.That(read.IsError).IsFalse();
            await Assert.That(read.Read).IsNotNull();
            screen = read.Read!.Screen.Text;
        }

        await Assert.That(screen).Contains(EchoMarker).Because("Last server screen: " + screen);

        await session.ResizeAsync(ResizedCols, ResizedRows, cancellationToken);
        var resized = await ReadFirstResizeAsync(session, cancellationToken);
        await Assert.That(resized.Cols).IsEqualTo(ResizedCols);
        await Assert.That(resized.Rows).IsEqualTo(ResizedRows);

        return new TerminalDrive
        {
            Role = attachment.Role,
            InputProtocol = attachment.InputProtocol,
            Cols = resized.Cols,
            Rows = resized.Rows,
            Echoed = echoed,
            Screen = screen,
        };
    }

    /// <summary>
    /// Reads output frames until the concatenated bytes carry the marker. The bytes are decoded
    /// together rather than per frame: this family sends raw terminal output, which is free to
    /// split a multi-byte character - or the marker itself - across two frames.
    /// </summary>
    /// <param name="session">The attached session to read.</param>
    /// <param name="marker">The text the read is waiting for.</param>
    /// <param name="cancellationToken">The test's own deadline.</param>
    /// <returns>Everything the terminal sent, up to and including the marker.</returns>
    private static async Task<string> ReadUntilMarkerAsync(
        PersistentPtySession session, string marker, CancellationToken cancellationToken)
    {
        var output = new List<byte>();
        await foreach (var frame in session.ReadAsync(cancellationToken))
        {
            if (frame is not PersistentPtyOutputFrame chunk)
            {
                continue;
            }

            output.AddRange(chunk.Data.ToArray());
            var text = Encoding.UTF8.GetString([.. output]);
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return text;
            }
        }

        throw new InvalidOperationException(
            "The persistent terminal closed before it produced '" + marker + "'.");
    }

    /// <summary>
    /// Returns the server's first resize report. The first frame is the answer whatever it
    /// carries: a server that clamped the requested size to another one must fail the caller's
    /// assertion in seconds, naming both dimensions, rather than being read past until the test's
    /// token fires with nothing recorded. The enumeration the echo left behind was disposed by its
    /// own loop, so this one starts fresh on the same socket.
    /// </summary>
    private static async Task<PersistentPtyResizedFrame> ReadFirstResizeAsync(
        PersistentPtySession session, CancellationToken cancellationToken)
    {
        await foreach (var frame in session.ReadAsync(cancellationToken))
        {
            if (frame is PersistentPtyResizedFrame resized)
            {
                return resized;
            }
        }

        throw new InvalidOperationException(
            "The persistent terminal closed before it reported any resize; the session asked for " +
            Number(ResizedCols) + "x" + Number(ResizedRows) + ".");
    }

    /// <summary>Renders one number for the console line and the failure text, culture-free.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders where a marker was found inside a real answer, and how much of an answer there
    /// was. Printing the marker constant instead would produce the same line whether the terminal
    /// answered or not, which is exactly what stops such a line from being evidence.
    /// </summary>
    private static string Observed(string text, string marker) =>
        marker + "@" + Number(text.LastIndexOf(marker, StringComparison.Ordinal)) + "/" + Number(text.Length);

    /// <summary>Renders an observed nullable identity, so an absent one prints as absent.</summary>
    private static string Describe(string? value) => value ?? "<null>";

    /// <summary>What the live drive observed; every member is printed evidence.</summary>
    private sealed record TerminalDrive
    {
        /// <summary>The role the server granted this connection.</summary>
        public required PersistentPtyRole Role { get; init; }

        /// <summary>The input protocol the server negotiated.</summary>
        public required int InputProtocol { get; init; }

        /// <summary>The columns the server reported after the resize.</summary>
        public required int Cols { get; init; }

        /// <summary>The rows the server reported after the resize.</summary>
        public required int Rows { get; init; }

        /// <summary>The terminal output the echo read collected, decoded together.</summary>
        public required string Echoed { get; init; }

        /// <summary>The screen text the HTTP read route answered with.</summary>
        public required string Screen { get; init; }
    }
}
