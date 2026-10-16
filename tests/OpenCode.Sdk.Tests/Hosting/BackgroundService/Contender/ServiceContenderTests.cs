using System.Text;
using OpenCode.Sdk.Internal;

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

    private static ServiceContender Contender(string stderr, string? redact = Ticket) =>
        new(Environment.ProcessId, new MemoryStream(Encoding.UTF8.GetBytes(stderr)), process: null, redact);
}
