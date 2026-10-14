using System.Globalization;
using System.Security.Cryptography;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

public partial class OpenCodeServer
{
    private const int StderrRetainedLines = 40;

    /// <summary>The reference client's exact standalone argv tail, appended to every command.</summary>
    private static readonly string[] LauncherArguments = ["--stdio", "--port", "0"];

    /// <summary>
    /// Gets the owned child's own exit, as the launcher observed it; friend-assembly test seam for
    /// how the disposal ladder ended the server.
    /// </summary>
    internal Task<ChildExitStatus> ChildExited =>
        _child?.Exited ?? throw new InvalidOperationException("This handle owns no process.");

    /// <summary>
    /// Gets a task that completes once the server <see cref="StartAsync"/> started has ended,
    /// whether it ended on its own or disposal ended it, with how it ended: an exit code, a
    /// terminating signal, or an unknown status. It is a task, not an event: await it, or
    /// <c>WaitAsync</c> it with a token. It completes once the exit is observed and the server's
    /// output has ended, or one second after the exit when a descendant still holds the output
    /// open, as upstream's standalone server reports <c>exited</c>. It never faults and is never
    /// canceled: a signal that ends the server is a value, not an exception.
    /// </summary>
    /// <exception cref="InvalidOperationException">This handle owns no process (<see cref="OwnsProcess"/> is false): a
    /// service <see cref="DiscoverAsync"/> or <see cref="EnsureAsync"/> found is shared, and its end is not observed.</exception>
    public virtual Task<OpenCodeServerExit> Exited =>
        _exited ?? throw (_endpoint is null
            ? MockSeam.CreateError("OpenCodeServer", "Exited")
            : new InvalidOperationException("This handle owns no process, so its end is not observed: only a server StartAsync started reports Exited."));

    /// <summary>
    /// Starts a fresh private standalone server: resolves the command the way a shell would,
    /// spawns it with <c>--stdio --port 0</c> appended and the generated lease credential in the
    /// child environment, then waits for the JSON readiness line. On any failure that reached a
    /// child, the child is ended before the method throws. On Windows the server is created
    /// suspended, placed in the process-wide job that ends it with its owner, and only then
    /// resumed; a failed start ends it the way disposal does, with <c>taskkill /T /F</c> at once and
    /// again after <see cref="OpenCodeServerOptions.GracefulShutdownTimeout"/> when it is still
    /// running, so a canceled or timed-out start can take up to that grace plus 26 seconds to throw.
    /// On Linux and macOS the server is spawned in a session of its own, and a failed start ends it
    /// the way disposal does: <c>SIGTERM</c> to its process group, the grace, then <c>SIGKILL</c>. A
    /// canceled or timed-out start can therefore take up to that grace plus 16 seconds to throw: at
    /// most 10 for the server's exit after <c>SIGKILL</c>, 1 for draining its output, and 5 for
    /// releasing the output readers.
    /// </summary>
    /// <param name="options">The launch options; null uses the defaults.</param>
    /// <param name="cancellationToken">The cancellation token ending the wait for readiness.</param>
    /// <returns>The started server, disposed by the caller.</returns>
    /// <exception cref="ArgumentException">The options are unusable: an empty command, a blank command entry, a non-positive readiness timeout, a negative grace, an output collector an earlier start already bound, or a NUL in a command entry or an environment entry.</exception>
    /// <exception cref="OpenCodeServerException">The command did not resolve on PATH, a leading argument was refused for a Windows batch shim, the platform is neither Windows, Linux, nor macOS, or the process could not start, could not be placed in the job that ends it with its owner (Windows), exited or closed its stdout before readiness, timed out, or broke the readiness contract.</exception>
    public static Task<OpenCodeServer> StartAsync(
        OpenCodeServerOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StartWithSeamsAsync(options, LauncherSeams.ForCurrentProcess(), cancellationToken);

    /// <summary>
    /// The seam-injected start the tests use, the way <see cref="EnsureWithSeamsAsync"/> keeps its
    /// seams off the public options: a live proof substitutes the Windows tree kill, a POSIX seam,
    /// or the host environment. The public door passes the platform's own.
    /// </summary>
    /// <param name="options">The launch options; null uses the defaults.</param>
    /// <param name="seams">The seams for this start and the server's disposal.</param>
    /// <param name="cancellationToken">The cancellation token ending the wait for readiness.</param>
    /// <returns>The started server, disposed by the caller.</returns>
    internal static async Task<OpenCodeServer> StartWithSeamsAsync(
        OpenCodeServerOptions? options,
        LauncherSeams seams,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seams);

        options ??= new OpenCodeServerOptions();
        var command = SnapshotCommand(options);
        var readinessTimeout = options.ReadinessTimeout;
        var output = BindOutput(options);

        var password = GeneratePassword();

        // Ownership stays local until the very end: every failure path throws through this
        // try, and the finally is the single place that releases the child on that path. The
        // flag is set only once the new OpenCodeServer has taken ownership on success
        // (TransportPolicy.CreateOwnedHttpClient's handler-ownership idiom, mirrored here).
        OwnedServerChild? child = null;
        var started = false;
        try
        {
            // Once per start, before anything is spawned: what the process starts, and what a
            // failure names, is the resolved target rather than the bare name the caller wrote.
            var executable = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess())
                .Resolve(command[0]);
            var stderrGate = new object();
            var stderrTail = new Queue<string>(StderrRetainedLines);
            var readyLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (onStandardOutput, onStandardError) = CreateOutputHandlers(readyLine, stderrGate, stderrTail, output);
            child = await ServerChild.LaunchAsync(
                new ServerChildStart
                {
                    Executable = executable,
                    SuppliedArguments = [.. command.Skip(1)],
                    LauncherArguments = LauncherArguments,
                    Environment = options.Environment,
                    Password = password,
                    WorkingDirectory = options.WorkingDirectory,
                    GracefulShutdownTimeout = options.GracefulShutdownTimeout,
                    Output = output,
                    OnStandardOutput = onStandardOutput,
                    OnStandardError = onStandardError,
                },
                seams).ConfigureAwait(false);

            var line = await WaitForReadyLineAsync(
                child, readyLine, readinessTimeout, stderrGate, stderrTail, cancellationToken).ConfigureAwait(false);
            if (!ServerReadyLine.TryParse(line, out var endpoint))
            {
                await child.EndFailedStartAsync().ConfigureAwait(false);
                throw new OpenCodeServerException(
                    $"The server's first stdout line is not the JSON readiness contract: '{line}'.{DescribeStderr(stderrGate, stderrTail)}");
            }

            var server = new OpenCodeServer(child, endpoint, password);
            started = true;
            return server;
        }
        finally
        {
            if (!started)
            {
                // A failed start: the child was ended and its output drained (as far as the
                // bound allowed) on the way here. When an unexpected failure skipped that, the
                // POSIX release ends the child first. Releasing the readers ends any read still
                // waiting, so no reader outlives the start and what the collector holds is final.
                if (child is not null)
                {
                    await child.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    output?.Complete();
                }
            }
        }
    }

    private static async Task<OpenCodeServerExit> TranslateAsync(Task<ChildExitStatus> closed) =>
        OpenCodeServerExit.From(await closed.ConfigureAwait(false), PosixSignalNames.ForCurrentProcess);

    private static OpenCodeServerOutput? BindOutput(OpenCodeServerOptions options)
    {
        var output = options.Output;
        if (output is not null && !output.TryBind())
        {
            throw new ArgumentException(
                "OpenCodeServerOptions.Output is already bound to an earlier start; create a new OpenCodeServerOutput per start.",
                nameof(options));
        }

        return output;
    }

    private static (Action<string> StandardOutput, Action<string> StandardError) CreateOutputHandlers(
        TaskCompletionSource<string> readyLine,
        object stderrGate,
        Queue<string> stderrTail,
        OpenCodeServerOutput? output)
    {
        // Continuous drain: the first stdout line is the readiness contract; every later stdout
        // write is read and, unless a collector retains it, dropped so a chatty server can never
        // fill the pipe and wedge the probe (the reference keeps draining too, in standalone.ts).
        // The collector's append takes only its own bounded-data lock.
        void OnStandardOutput(string line)
        {
            // Retain first, then signal: the readiness continuation can run the moment the
            // result is set, and a snapshot taken right after StartAsync returns must already
            // hold the line that made it return.
            output?.AppendStandardOutput(line);
            _ = readyLine.TrySetResult(line);
        }

        void OnStandardError(string line)
        {
            lock (stderrGate)
            {
                stderrTail.Enqueue(line);
                if (stderrTail.Count > StderrRetainedLines)
                {
                    _ = stderrTail.Dequeue();
                }
            }

            output?.AppendStandardError(line);
        }

        return (OnStandardOutput, OnStandardError);
    }

    private static async Task<string> WaitForReadyLineAsync(
        OwnedServerChild child,
        TaskCompletionSource<string> readyLine,
        TimeSpan readinessTimeout,
        object stderrGate,
        Queue<string> stderrTail,
        CancellationToken cancellationToken)
    {
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(readinessTimeout);
        ReadinessOutcome outcome;
        try
        {
            outcome = await child.WaitForReadinessAsync(readyLine.Task, readiness.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await child.EndFailedStartAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The server start was canceled.", exception, cancellationToken);
            }

            throw new OpenCodeServerException(
                $"The server did not report readiness within {readinessTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.{DescribeStderr(stderrGate, stderrTail)}",
                exception);
        }

        if (outcome.ReadyLine is { } line)
        {
            return line;
        }

        var ending = outcome.Exit is { } exit ? exit.Describe() : "closed its standard output";
        throw new OpenCodeServerException(
            $"The server {ending} before reporting readiness.{DescribeStderr(stderrGate, stderrTail)}");
    }

    private static string[] SnapshotCommand(OpenCodeServerOptions options)
    {
        var command = options.Command;
        if (command is not { Count: > 0 })
        {
            throw new ArgumentException(
                "OpenCodeServerOptions.Command needs the executable and its leading arguments.", nameof(options));
        }

        var snapshot = new string[command.Count];
        for (var index = 0; index < command.Count; index++)
        {
            var entry = command[index];
            if (string.IsNullOrWhiteSpace(entry))
            {
                throw new ArgumentException("OpenCodeServerOptions.Command entries cannot be blank.", nameof(options));
            }

            snapshot[index] = entry;
        }

        return snapshot;
    }

    private static string GeneratePassword()
    {
        var bytes = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);

        // base64url, mirroring the reference client's randomBytes(32).toString("base64url").
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string DescribeStderr(object gate, Queue<string> tail)
    {
        lock (gate)
        {
            return tail.Count == 0 ? string.Empty : string.Concat(" Recent stderr: ", string.Join(" | ", tail));
        }
    }
}
