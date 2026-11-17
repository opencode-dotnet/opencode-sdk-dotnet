using System.Globalization;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

[NotInParallel]
public sealed class ServiceCliRunnerLiveTests
{
    private readonly RealFileSystem _files = new();

    [Test]
    [Arguments("zero")]
    [Arguments("error")]
    [Arguments("arguments")]
    [Arguments("lines")]
    [Arguments("characters")]
    [Arguments("stdout")]
    [Timeout(90_000)]
    public async Task RunAsync_Should_Run_And_Drain_A_Real_Child(string kind, CancellationToken cancellationToken)
    {
        using var root = new TestRunRoot(_files);
        using var caller = new CancellationTokenSource();
        var callerToken = caller.Token;
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(45));
        cleanup.Own("cancel runner", async _ => await caller.CancelOnWorkerAsync());
        Exception? primaryFailure = null;
        try
        {
            var script = kind switch
            {
                "zero" => "process.exit(0)",
                "error" => "console.error('cli-error-witness'); process.exit(3)",
                "arguments" => "console.error(process.argv.slice(-4).join(' ')+';'+process.env.SDK_CONFIG_ENTRY+';'+typeof process.env.OPENCODE_PASSWORD); process.exit(7)",
                "lines" => "for (let i=0;i<50;i++) console.error('line-'+i); process.exit(1)",
                "characters" => "console.error('x'.repeat(9000)); process.exit(1)",
                "stdout" => "process.stdout.write('x'.repeat(1024*1024)+'\\n', () => process.exit(0))",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var running = RunAsync(script, root.Path, callerToken);
            Task[] tasks = [running];
            if (kind is "zero" or "stdout")
            {
                cleanup.Own("runner", running, failure => failure.CancellationToken == callerToken);
                await Task.WhenAll(tasks).WaitAsync(HangGuard.Bound, cancellationToken);
            }
            else
            {
                var failure = await Assert.That(async () => await Task.WhenAll(tasks).WaitAsync(HangGuard.Bound, cancellationToken)).Throws<OpenCodeServerException>();
                var code = kind switch { "error" => 3, "arguments" => 7, _ => 1 };
                var tail = kind switch
                {
                    "error" => "cli-error-witness",
                    "arguments" => "service set remote true;checked;undefined",
                    "lines" => string.Join(" | ", Enumerable.Range(10, 40).Select(static number => "line-" + number.ToString(CultureInfo.InvariantCulture))),
                    _ => new string('x', 8192),
                };
                await Assert.That(failure!.Message).IsEqualTo(
                    $"CLI command '{new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve("bun").Path}' " +
                    $"exited with code {code.ToString(CultureInfo.InvariantCulture)}. Recent stderr: {tail}");
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(90_000)]
    public async Task RunAsync_Should_End_A_Real_Child_When_Cancelled(CancellationToken cancellationToken)
    {
        using var root = new TestRunRoot(_files);
        using var caller = new CancellationTokenSource();
        var callerToken = caller.Token;
        using var listener = new HeldHeadersListener();
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(45));
        cleanup.Own("cancel runner", async _ => await caller.CancelOnWorkerAsync());
        Exception? primaryFailure = null;
        try
        {
            var script = "require('net').connect(" + listener.Endpoint.Port.ToString(CultureInfo.InvariantCulture) +
                ", '127.0.0.1', function() { this.write(process.pid+'\\n'); }); process.stdin.resume();";
            var running = RunAsync(script, root.Path, callerToken);
            cleanup.Own("runner", running, failure => failure.CancellationToken == callerToken);
            using var peer = await listener.AcceptAsync().WaitAsync(HangGuard.Bound, cancellationToken);
            using var reader = new StreamReader(peer.GetStream(), Encoding.ASCII);
#if NET
            var line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(HangGuard.Bound, cancellationToken);
#else
            // Downlevel StreamReader has no cancellable read; the outer wait bounds the witness.
            var line = await reader.ReadLineAsync().WaitAsync(HangGuard.Bound, cancellationToken);
#endif
            var pid = int.Parse(line ?? throw new InvalidOperationException("The child closed its witness without reporting a PID."),
                CultureInfo.InvariantCulture);
            cleanup.Own("end witness child", _ => { ProcessObservation.KillIfRunning(pid); return Task.CompletedTask; });
            await Assert.That(ProcessObservation.IsRunning(pid)).IsTrue();
            await caller.CancelOnWorkerAsync();
            Task[] tasks = [running];
            var failure = await Assert.That(async () => await Task.WhenAll(tasks).WaitAsync(HangGuard.Bound, cancellationToken)).Throws<OperationCanceledException>();
            await Assert.That(failure!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(ProcessObservation.IsRunning(pid)).IsFalse();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private Task RunAsync(string script, string runRoot, CancellationToken cancellationToken)
    {
        var environment = ServerIsolation.For(_files, runRoot).Environment;
        environment["SDK_CONFIG_ENTRY"] = "checked";
        var executable = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve("bun");
        return new ServiceCliRunner(LauncherSeams.ForCurrentProcess()).RunAsync(
            executable, ["-e", script, "service", "set", "remote", "true"], environment, cancellationToken);
    }
}
