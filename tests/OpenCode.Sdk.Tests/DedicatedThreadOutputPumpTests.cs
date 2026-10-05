using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The Windows launcher pump against a real child whose redirected pipes
/// <see cref="Process"/> created: while the child is alive and silent no end-of-stream arrives,
/// the bounded drain reports that, and the release still ends both reader threads, so no reader
/// outlives its owner even when something keeps the write ends open. Each ended reader has closed
/// its pipe's read handle, which <see cref="Process"/> itself never closes for a stream read
/// synchronously.
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
            _ = ProcessTreeTerminator.Platform.TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task ReleaseAsync_Should_Close_Both_Read_Handles_While_The_Child_Still_Holds_The_Pipes(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
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
        DedicatedThreadOutputPump? pump = null;
        try
        {
            var (standardOutput, standardError) = ReadHandles(process);
            pump = DedicatedThreadOutputPump.Begin(process, static _ => { }, static _ => { });

            await pump.ReleaseAsync().WaitAsync(ReleaseBound, cancellationToken);

            // The process is still alive and not disposed: the readers closed the handles.
            await Assert.That(standardOutput.IsClosed).IsTrue();
            await Assert.That(standardError.IsClosed).IsTrue();
        }
        finally
        {
            _ = ProcessTreeTerminator.Platform.TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (pump is not null)
            {
                await pump.ReleaseAsync();
            }
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task ReadersEnded_Should_Complete_With_Both_Read_Handles_Closed_At_End_Of_Stream(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bun",
            ArgumentList = { "-e", "console.log('out'); console.error('err');" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        DedicatedThreadOutputPump? pump = null;
        try
        {
            var (standardOutput, standardError) = ReadHandles(process);
            pump = DedicatedThreadOutputPump.Begin(process, static _ => { }, static _ => { });

            await pump.ReadersEnded.WaitAsync(ReleaseBound, cancellationToken);

            await Assert.That(standardOutput.IsClosed).IsTrue();
            await Assert.That(standardError.IsClosed).IsTrue();
        }
        finally
        {
            // The child exits on its own; this ends it when an assertion or the wait failed first.
            _ = ProcessTreeTerminator.Platform.TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (pump is not null)
            {
                await pump.ReleaseAsync();
            }
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

    /// <summary>
    /// The parent's read handles of both redirected pipes, taken before any reader starts. On
    /// Windows <see cref="Process"/> wraps each in a <see cref="FileStream"/>.
    /// </summary>
    private static (SafeFileHandle StandardOutput, SafeFileHandle StandardError) ReadHandles(Process process) =>
        (((FileStream)process.StandardOutput.BaseStream).SafeFileHandle,
            ((FileStream)process.StandardError.BaseStream).SafeFileHandle);
}
