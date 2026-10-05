using System.Globalization;
using System.Text;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Posix;

/// <summary>
/// The shipped <see cref="PosixSpawn"/> against a real shell: every descriptor routed to a pipe,
/// the routes the background-service contender does not use itself. The contender's own tests
/// prove the <c>/dev/null</c> routes, the session, and the signal state through the same spawn.
/// Windows has no POSIX spawn, and the arm there asserts the refusal.
/// </summary>
// On macOS a pipe is created and then marked close-on-exec in two steps, so a spawn running in
// the same host between them can inherit this test's write ends and hold its reads open; the
// class runs alone until every spawn takes one lock around pipe creation.
[NotInParallel]
public sealed class PosixSpawnTests
{
    /// <summary>Echoes one line of standard input to standard output and writes a marker to standard error.</summary>
    private const string EchoScript = "read line; printf '%s\\n' \"$line\"; printf 'stderr marker\\n' >&2";

    private const string Line = "through the stdin pipe";

    private const string StderrMarker = "stderr marker";

    [Test]
    [Timeout(60_000)]
    public async Task Spawn_Should_Route_Every_Descriptor_Through_Its_Own_Pipe(CancellationToken cancellationToken)
    {
        var request = new PosixSpawnRequest
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-c", EchoScript],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            StandardInput = ChildStreamRoute.Pipe,
            StandardOutput = ChildStreamRoute.Pipe,
            StandardError = ChildStreamRoute.Pipe,
        };
        if (OperatingSystem.IsWindows())
        {
            var refusal = await Assert.That(() => new PosixSpawn().Spawn(request)).Throws<PlatformNotSupportedException>();
            Console.WriteLine("branch: Windows — " + refusal!.Message);
            return;
        }

        var child = new PosixSpawn().Spawn(request);
        var reaped = false;
        using var output = new StreamReader(child.StandardOutput!, Encoding.UTF8);
        using var error = new StreamReader(child.StandardError!, Encoding.UTF8);
        try
        {
            using (var input = child.StandardInput!)
            {
                await input.WriteAsync(Encoding.UTF8.GetBytes(Line + "\n").AsMemory(), cancellationToken);
            }

            var echoed = await output.ReadToEndAsync(cancellationToken);
            var stderr = await error.ReadToEndAsync(cancellationToken);

            // The spawn leaves the child to its parent to reap; both pipes reached end-of-stream,
            // so the blocking wait returns at once.
            var waited = PosixInterop.WaitPid(child.ProcessId, out var status, 0);
            reaped = waited == child.ProcessId;

            await Assert.That(echoed).IsEqualTo(Line + "\n");
            await Assert.That(stderr).IsEqualTo(StderrMarker + "\n");
            await Assert.That(reaped).IsTrue();
            await Assert.That(ChildExitStatus.FromWaitStatus(status).ExitCode).IsEqualTo(0);
            Console.WriteLine("branch: Unix — pid " + child.ProcessId.ToString(CultureInfo.InvariantCulture) + " echoed its stdin pipe onto its stdout pipe");
        }
        finally
        {
            if (!reaped)
            {
                ProcessObservation.KillIfRunning(child.ProcessId);
                _ = PosixInterop.WaitPid(child.ProcessId, out _, 0);
            }
        }
    }
}
