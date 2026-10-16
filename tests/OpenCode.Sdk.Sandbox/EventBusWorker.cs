using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenCode.Sdk.Sandbox;

internal sealed partial class EventBusWorker(
    OpenCodeClient client,
    EventsClient events,
    IHostApplicationLifetime applicationLifetime,
    ILogger<EventBusWorker> logger) : BackgroundService
{
    public Exception? Failure { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var health = await client.Server.GetInfoAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
            LogServerIdentity(logger, health.ServerInfo.Version, health.ServerInfo.Pid);
            LogOpeningBus(logger);

            await foreach (var item in events.SubscribeAsync(cancellationToken: stoppingToken).ConfigureAwait(false))
            {
                LogFrame(logger, item.GetType().Name, item.Type);
            }

            applicationLifetime.StopApplication();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            Failure = exception;
            throw;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Connected to opencode {Version} process {Pid}")]
    private static partial void LogServerIdentity(ILogger logger, string version, long pid);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Opening the volatile global event bus; trigger server activity and stop the host with Ctrl+C")]
    private static partial void LogOpeningBus(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Received typed global event frame {FrameType} with tag {EventType}")]
    private static partial void LogFrame(ILogger logger, string frameType, string eventType);
}
