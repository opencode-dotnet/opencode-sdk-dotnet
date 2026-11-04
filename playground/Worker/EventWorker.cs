using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk;

namespace OpenCode.Playground.Worker;

/// <summary>Starts its own server, logs the server's events, and stops the host when the server ends under it.</summary>
/// <param name="lifetime">Stops the host.</param>
/// <param name="logger">Where the events go.</param>
internal sealed partial class EventWorker(IHostApplicationLifetime lifetime, ILogger<EventWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A private server on your normal opencode data. It ends when the worker stops.
            await using var server = await OpenCodeServer.StartAsync(cancellationToken: stoppingToken);
            using var client = server.CreateClient();
            LogStarted(server.Endpoint, server.ProcessId);

            using var watching = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
            var exitWatch = StopWhenServerExitsAsync(server.Exited, watching.Token);
            try
            {
                // The live bus: it reconnects by itself, and ends when the host stops.
                await foreach (var @event in client.Events.SubscribeAsync(cancellationToken: stoppingToken))
                {
                    LogEvent(@event.Type);
                }
            }
            finally
            {
                await watching.CancelAsync();
                await exitWatch;
            }
        }
        catch (Exception failure)
        {
            if (failure is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                return;
            }

            Environment.ExitCode = 1;
            lifetime.StopApplication();
            LogFailed(failure);
        }
    }

    private async Task StopWhenServerExitsAsync(Task<OpenCodeServerExit> exited, CancellationToken hostStoppingToken)
    {
        OpenCodeServerExit exit;
        try
        {
            exit = await exited.WaitAsync(hostStoppingToken);
        }
        catch (OperationCanceledException) when (hostStoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (hostStoppingToken.IsCancellationRequested)
        {
            return;
        }

        // Like opencode's own clients, a worker that owns its server doesn't restart it: it fails, and the service manager restarts the worker.
        Environment.ExitCode = 1;
        lifetime.StopApplication();
        LogServerExited(exit);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "worker: server {Endpoint} started, pid {ProcessId}")]
    private partial void LogStarted(Uri endpoint, int processId);

    [LoggerMessage(Level = LogLevel.Information, Message = "event: {Type}")]
    private partial void LogEvent(string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "worker: the server exited ({Exit}); stopping with exit code 1")]
    private partial void LogServerExited(OpenCodeServerExit exit);

    [LoggerMessage(Level = LogLevel.Error, Message = "worker: failed; stopping with exit code 1")]
    private partial void LogFailed(Exception failure);
}
