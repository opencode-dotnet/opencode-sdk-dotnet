using System.Globalization;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Contender;

/// <summary>
/// The contender's retained stderr as diagnostics read it. The stderr is an in-memory stream, which
/// the drain consumes to its end inside the constructor, so every verdict is decided before the
/// first assertion. The pid is this process, never a child of it: a POSIX exit poll reads that as
/// reaped elsewhere, and a Windows poll has no handle to read.
/// </summary>
public sealed class ServiceContenderTests
{
    /// <summary>The pinned client's <c>stderrLimit</c> (<c>service-contender.ts</c>).</summary>
    private const int StderrLimit = 8 * 1024;

    private const string Ticket = "{\"source\":\"sdk-test\",\"secret\":\"0123456789abcdef0123456789abcdef\"}";

    [Test]
    public async Task Stderr_Should_Redact_Every_Whole_Ticket()
    {
        using var contender = Contender("before " + Ticket + " middle " + Ticket + " after");

        await Assert.That(contender.Stderr).IsEqualTo("before [redacted] middle [redacted] after");
    }

    /// <summary>
    /// The 8 KiB edge falls inside the ticket: one byte, a few bytes, or all but one of its 65
    /// bytes lie before the edge.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(13)]
    [Arguments(64)]
    public async Task Stderr_Should_Redact_A_Ticket_The_Tail_Cut_Splits(int bytesBeforeTheCut)
    {
        var after = new string('b', StderrLimit - (Ticket.Length - bytesBeforeTheCut));
        using var contender = Contender(new string('a', 100) + Ticket + after);

        await Assert.That(contender.Stderr).IsEqualTo("[redacted]" + after);
    }

    [Test]
    public async Task Stderr_Should_Redact_A_Ticket_The_Pipe_Ended_Inside()
    {
        using var contender = Contender("starting " + Ticket[..20]);

        await Assert.That(contender.Stderr).IsEqualTo("starting [redacted]");
    }

    /// <summary>A ticket whose end repeats its start: the occurrences overlap and cover one run.</summary>
    [Test]
    public async Task Stderr_Should_Redact_Overlapping_Tickets_As_One_Run()
    {
        using var contender = Contender("x ababab y", redact: "abab");

        await Assert.That(contender.Stderr).IsEqualTo("x [redacted] y");
    }

    /// <summary>A ticket longer than the rendered 8 KiB that the pipe ended inside: no byte of it shows.</summary>
    [Test]
    public async Task Stderr_Should_Redact_An_Unfinished_Ticket_Longer_Than_The_Tail()
    {
        var body = new string('x', StderrLimit);
        using var contender = Contender("start A" + body, redact: "A" + body + "Z");

        await Assert.That(contender.Stderr).IsEqualTo("[redacted]");
    }

    [Test]
    public async Task Stderr_Should_Keep_The_Final_8_KiB_Unchanged_Without_A_Ticket()
    {
        var kept = new string('k', StderrLimit);
        using var contender = Contender(new string('d', 100) + kept, redact: null);

        await Assert.That(contender.Stderr).IsEqualTo(kept);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task Stderr_Should_Keep_Text_Unredacted_When_The_Ticket_Is_Null_Or_Empty(string? redact)
    {
        const string text = "before ticket after";
        using var contender = Contender(text, redact);

        await Assert.That(contender.Stderr).IsEqualTo(text);
    }

    [Test]
    public async Task TryGetFailure_Should_Report_A_Read_Fault_Until_Disposal()
    {
        var failure = new IOException("The stderr read failed.");
        using var contender = Contender(stream: new FaultingStderr(failure));

        var exception = contender.TryGetFailure();

        await Assert.That(exception).IsTypeOf<OpenCodeServerException>();
        await Assert.That(exception!.Message).IsEqualTo(
            $"The background service contender (pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}) failed to start.");
        await Assert.That(exception.InnerException).IsSameReferenceAs(failure);
        await Assert.That(contender.Finished).IsTrue();

        contender.Dispose();

        await Assert.That(contender.TryGetFailure()).IsNull();
    }

    /// <summary>Our own pid cannot be our child: POSIX waitpid reports ECHILD, not an exit code.</summary>
    [Test]
    public async Task Finished_Should_Report_An_Unknown_Exit_At_Stderr_End_When_The_Pid_Is_Not_Our_Child()
    {
        if (OperatingSystem.IsWindows())
        {
            BranchReport.Print("Windows — no faithful fake handle for the POSIX ECHILD observation");
            return;
        }

        using var contender = Contender();

        await Assert.That(contender.Finished).IsTrue();
        await Assert.That(contender.ExitedZero).IsFalse();
        await Assert.That(contender.TryGetFailure()).IsNull();
        BranchReport.Print("POSIX — ECHILD is an unknown exit, finished at EOF and not a failure");
    }

    [Test]
    public async Task Release_Should_Drop_The_Retained_Stderr()
    {
        using var contender = Contender("retained stderr");

        contender.Release();

        await Assert.That(contender.Stderr).IsEqualTo(string.Empty);
    }

    private static ServiceContender Contender(string stderr = "", string? redact = Ticket, Stream? stream = null) =>
        new(Environment.ProcessId, stream ?? new MemoryStream(Encoding.UTF8.GetBytes(stderr)), process: null, redact);

    /// <summary>Both target-framework read paths throw the same fault before any read suspends.</summary>
    private sealed class FaultingStderr(IOException failure) : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw failure;

#if NET
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure;
#endif
    }
}
