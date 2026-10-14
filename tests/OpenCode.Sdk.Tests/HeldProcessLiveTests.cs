using System.Diagnostics;
using System.Globalization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The end a run root's teardown waits for: a bun process that holds a SQLite database in WAL mode
/// and a few hundred open files — the handle load a source-run daemon carries — is ended, and once
/// the wait returns every file it held can be deleted at once. On Windows the exit code is set
/// before those handles close; a wait that stopped there let a run root's deletion leave the dying
/// daemon's files behind.
/// </summary>
[NotInParallel]
public sealed class HeldProcessLiveTests
{
    private const int Rounds = 10;
    private static readonly TimeSpan TerminationBound = TimeSpan.FromSeconds(15);
    private readonly RealFileSystem _fileSystem = new();

    [Test]
    [Timeout(180_000)]
    public async Task EndAllAsync_Should_Return_Once_The_Ended_Process_Holds_No_File(CancellationToken cancellationToken)
    {
        var bun = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess())
            .Resolve(new PinnedServerCommand(_fileSystem).Resolve()[0])
            .Path;
        var directory = _fileSystem.Path.Combine(
            _fileSystem.Path.GetTempPath(), "opencode-sdk-held-process-" + Guid.NewGuid().ToString("N"));
        _ = _fileSystem.Directory.CreateDirectory(directory);

        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var number = round.ToString(CultureInfo.InvariantCulture);
                var database = _fileSystem.Path.Combine(directory, "round-" + number + ".db");
                using var child = Process.Start(new ProcessStartInfo(bun)
                {
                    ArgumentList = { "-e", HoldingScript(database) },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                })!;
                await Assert.That(await child.StandardOutput.ReadLineAsync(cancellationToken)).IsEqualTo("ready");

                var mark = ProcessMark.TryRead(_fileSystem, child.Id);
                await Assert.That(mark).IsNotNull();

                await Assert.That(await HeldProcess.EndAllAsync(_fileSystem, [mark!.Value], TerminationBound)).IsTrue();
                await Assert.That(TryDelete(database + "-wal") && TryDelete(database))
                    .IsTrue().Because("round " + number + ": the ended process still held its files");
            }
        }
        finally
        {
            _ = BestEffortDelete.TryDeleteTree(_fileSystem, directory);
        }
    }

    /// <summary>
    /// A held process released while its wait is still registered: the wait still reports the
    /// process's end. On Windows the wait's signal owns a handle of its own, never the handle it was
    /// made from, so the release cannot close the handle the thread pool's wait thread waits on,
    /// which would end this host; a second registration asks that thread to wait again after the
    /// release. On Linux and macOS the wait observes the process, and the release must not stop that
    /// either.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task WaitForTerminationAsync_Should_Survive_The_Process_Being_Released_While_It_Waits(CancellationToken cancellationToken)
    {
        using var child = Process.Start(LongRunningChild())!;
        using var other = new ManualResetEvent(initialState: false);
        RegisteredWaitHandle? waitAgain = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var signal = new HeldProcess.ProcessSignal(child.SafeHandle);
                await Assert.That(signal.SafeWaitHandle.DangerousGetHandle()).IsNotEqualTo(child.SafeHandle.DangerousGetHandle());
            }

            var mark = ProcessMark.TryRead(_fileSystem, child.Id);
            await Assert.That(mark).IsNotNull();
            using var held = HeldProcess.TryHold(_fileSystem, mark!.Value);
            await Assert.That(held).IsNotNull();

            var ended = held!.WaitForTerminationAsync(TerminationBound, cancellationToken);
            held.Dispose();
            waitAgain = ThreadPool.RegisterWaitForSingleObject(other, static (_, _) => { }, state: null, Timeout.Infinite, executeOnlyOnce: true);
            child.Kill();

            await Assert.That(await ended).IsTrue();
        }
        finally
        {
            _ = waitAgain?.Unregister(null);
            ProcessObservation.KillIfRunning(child.Id);
            _ = await ProcessObservation.ObserveExitWithinAsync(child, TerminationBound, CancellationToken.None);
        }
    }

    /// <summary>A child that runs two minutes unless ended, with no shell and no network beyond loopback.</summary>
    private ProcessStartInfo LongRunningChild() =>
        OperatingSystem.IsWindows()
            ? new ProcessStartInfo(_fileSystem.Path.Combine(Environment.SystemDirectory, "ping.exe"))
            {
                ArgumentList = { "-n", "120", "127.0.0.1" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
            : new ProcessStartInfo("sleep")
            {
                ArgumentList = { "120" },
                UseShellExecute = false,
            };

    /// <summary>
    /// A bun program that opens the database in WAL mode, writes to it in one transaction, holds
    /// three hundred more files, and waits. One transaction is one flush: a commit per row flushed
    /// the log two thousand times per round, which a hosted runner's disk turned into minutes.
    /// </summary>
    private static string HoldingScript(string database)
    {
        var path = database.Replace('\\', '/');
        return "const { Database } = require('bun:sqlite');"
            + $"const db = new Database('{path}'); db.exec('PRAGMA journal_mode=WAL'); db.exec('create table t(x)');"
            + "db.exec('BEGIN'); for (let i = 0; i < 2000; i++) db.exec('insert into t values(' + i + ')'); db.exec('COMMIT');"
            + $"const fs = require('fs'); for (let i = 0; i < 300; i++) fs.openSync('{path}.' + i, 'w');"
            + "console.log('ready'); setInterval(() => {}, 1000);";
    }

    private bool TryDelete(string path)
    {
        try
        {
            _fileSystem.File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
