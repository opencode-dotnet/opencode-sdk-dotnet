using System.Globalization;
using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The census the Windows handle proof rests on: each launcher kind it counts shows while a handle
/// of that kind is open and is gone once closed, and the runtime's own thread handles never show.
/// The controls run in the service fixture, a host where nothing else opens handles, against this
/// process as the foreign one; the written form reads back to the same census on every platform.
/// </summary>
public sealed class LauncherHandleCensusTests
{
    private static readonly RealFileSystem FileSystem = new();

    private static readonly IReadOnlyDictionary<string, string?> NoOverlay = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Positive: a handle on another process, a <c>SYNCHRONIZE</c>-only one, one on its thread, a
    /// pipe's two ends, and a job each add exactly one to their own count (two for the pipe), and
    /// closing them returns the census to where it was. Negative: two parked threads raise the
    /// host's whole handle count and leave the census unchanged.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Take_Should_Count_Each_Launcher_Kind_And_Not_The_Runtimes_Own_Threads(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the host descriptor reader counts pipes there");
            return;
        }

        var foreign = HostDescriptors.CurrentProcessId().ToString(CultureInfo.InvariantCulture);
        var result = await new ServiceFixtureCommand(FileSystem).RunAsync(["handle-census", foreign], NoOverlay, cancellationToken);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split(['='], 2))
            .Where(static pair => pair.Length == 2)
            .ToDictionary(static pair => pair[0], static pair => pair[1], StringComparer.Ordinal);
        var report = string.Join(" | ", result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var before = LauncherHandles.Parse(lines["before"]);
        var raw = lines["raw"].Split(',').Select(static count => int.Parse(count.Trim(), CultureInfo.InvariantCulture)).ToArray();

        var expectedOpen = before with
        {
            Processes = before.Processes + 1,
            ExitWaits = before.ExitWaits + 1,
            Threads = before.Threads + 1,
            Pipes = before.Pipes + 2,
            Jobs = before.Jobs + 1,
        };
        await Assert.That(LauncherHandles.Parse(lines["open"])).IsEqualTo(expectedOpen).Because(report);
        await Assert.That(LauncherHandles.Parse(lines["closed"])).IsEqualTo(before).Because(report);

        // The opened handles' own values, counted while open and again once closed: a value that no
        // longer names a handle counts as nothing, so a handle the finalizer closes mid-census is
        // never taken for a foreign one.
        await Assert.That(LauncherHandles.Parse(lines["mine-open"]))
            .IsEqualTo(new LauncherHandles(Processes: 1, ExitWaits: 1, Threads: 1, Pipes: 2, Jobs: 1)).Because(report);
        await Assert.That(LauncherHandles.Parse(lines["mine-closed"]))
            .IsEqualTo(new LauncherHandles(Processes: 0, ExitWaits: 0, Threads: 0, Pipes: 0, Jobs: 0)).Because(report);
        await Assert.That(LauncherHandles.Parse(lines["parked"])).IsEqualTo(before).Because(report);

        // Each parked thread holds at least its own thread handle; the census must not see one.
        await Assert.That(raw[1] - raw[0]).IsGreaterThanOrEqualTo(2).Because(report);
        BranchReport.Print("Windows — " + report);
    }

    /// <summary>A census reads back from the form the fixture prints, and anything else is refused.</summary>
    [Test]
    public async Task Parse_Should_Read_Back_The_Written_Form_And_Refuse_Anything_Else()
    {
        var census = new LauncherHandles(Processes: 1, ExitWaits: 2, Threads: 3, Pipes: 4, Jobs: 5);

        await Assert.That(census.ToString()).IsEqualTo("processes=1 exit-waits=2 threads=3 pipes=4 jobs=5");
        await Assert.That(LauncherHandles.Parse(census.ToString())).IsEqualTo(census);
        await Assert.That(() => LauncherHandles.Parse("processes=1 exit-waits=2 threads=3 pipes=4")).Throws<FormatException>();
        await Assert.That(() => LauncherHandles.Parse("processes=1 exit-waits=2 threads=3 jobs=5 pipes=4")).Throws<FormatException>();
        await Assert.That(() => LauncherHandles.Parse("processes=-1 exit-waits=2 threads=3 pipes=4 jobs=5")).Throws<FormatException>();
    }
}
