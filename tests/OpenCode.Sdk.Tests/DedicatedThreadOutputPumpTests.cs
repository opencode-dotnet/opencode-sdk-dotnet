using System.Diagnostics;
using OpenCode.Sdk.Internal;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The Windows launcher pump against a real child whose redirected pipes
/// <see cref="Process"/> created: while the child is alive and silent no end-of-stream arrives,
/// the bounded drain reports that, and the release still ends both reader threads, so no reader
/// outlives its owner even when something keeps the write ends open.
/// </summary>
[NotInParallel]
public sealed class DedicatedThreadOutputPumpTests
{
    private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(10);

    [Test]
    [Timeout(120_000)]
    public async Task ReleaseAsync_Should_End_Both_Readers_While_The_Child_Still_Holds_The_Pipes(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            // The dedicated-thread pump is the Windows pump; elsewhere Process's own readers hold no thread.
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bun",
            ArgumentList = { "-e", "setTimeout(() => {}, 120000)" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var pump = DedicatedThreadOutputPump.Begin(process, static _ => { }, static _ => { });

            var drained = await pump.DrainAsync(TimeSpan.FromMilliseconds(200));
            await Assert.That(drained).IsFalse();
            await Assert.That(pump.ReadersEnded.IsCompleted).IsFalse();

            await pump.ReleaseAsync().WaitAsync(ReleaseBound, cancellationToken);

            await Assert.That(pump.ReadersEnded.IsCompleted).IsTrue();
            await Assert.That(process.HasExited).IsFalse();
        }
        finally
        {
            _ = ProcessTreeTerminator.TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task DrainAsync_Should_Report_End_Of_Stream_Once_The_Child_Exits(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = new List<string>();
        var gate = new Lock();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bun",
            ArgumentList = { "-e", "console.log('out'); console.error('err');" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var pump = DedicatedThreadOutputPump.Begin(
            process,
            line => { lock (gate) { lines.Add("out:" + line); } },
            line => { lock (gate) { lines.Add("err:" + line); } });
        await process.WaitForExitAsync(cancellationToken);

        var drained = await pump.DrainAsync(ReleaseBound);
        await pump.ReleaseAsync();

        string[] delivered;
        lock (gate)
        {
            delivered = [.. lines];
        }

        await Assert.That(drained).IsTrue();
        await Assert.That(pump.ReadersEnded.IsCompleted).IsTrue();
        await Assert.That(delivered).IsEquivalentTo(["out:out", "err:err"], CollectionOrdering.Any);
    }
}
