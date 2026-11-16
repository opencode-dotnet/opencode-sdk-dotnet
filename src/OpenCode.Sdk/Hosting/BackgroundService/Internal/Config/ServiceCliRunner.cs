namespace OpenCode.Sdk.Internal;

/// <summary>Runs a CLI child to exit using the launcher's pipes, lease, bounded diagnostics and ownership ladder.</summary>
internal sealed class ServiceCliRunner(
    LauncherSeams seams, Func<ServerChildStart, LauncherSeams, Task<OwnedServerChild>> launch) : IServiceCliRunner
{
    /// <summary>Uses the shipped platform launch with the supplied root's environment and clock.</summary>
    /// <param name="seams">The launcher's root, shared with the configuration store.</param>
    public ServiceCliRunner(LauncherSeams seams) : this(seams, ServerChild.LaunchAsync)
    {
    }

    /// <inheritdoc />
    public async Task RunAsync(ResolvedExecutable executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var tail = BoundedLineBuffer.ForStderrTail();
        var gate = new Lock();
        var start = new ServerChildStart
        {
            Executable = executable,
            SuppliedArguments = arguments,
            LauncherArguments = [],
            Environment = environment,
            ForceStandardErrorPipe = true,
            GracefulShutdownTimeout = new OpenCodeServerOptions().GracefulShutdownTimeout,
            OnStandardOutput = static _ => { },
            OnStandardError = line =>
            {
                lock (gate)
                {
                    tail.Append(line);
                }
            },
        };
        cancellationToken.ThrowIfCancellationRequested();
        var child = await launch(start, seams).ConfigureAwait(false);
        var waiting = child.Closed.WaitAsync(cancellationToken);
        try
        {
            var status = await waiting.ConfigureAwait(false);
            if (status is not { ExitCode: 0, Signal: null })
            {
                lock (gate)
                {
                    throw new OpenCodeServerException(
                        $"CLI command '{executable.Path}' {status.Describe()}. Recent stderr: {string.Join(" | ", tail.Snapshot())}");
                }
            }
        }
        finally
        {
            try
            {
                if (waiting.IsCanceled)
                {
                    await child.EndFailedStartAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await child.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
