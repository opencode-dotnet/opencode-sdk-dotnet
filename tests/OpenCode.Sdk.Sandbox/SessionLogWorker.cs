using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Sandbox;

internal sealed partial class SessionLogWorker(
    OpenCodeClient client,
    SessionsClient sessions,
    ExperimentalClient experimental,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SessionLogWorker> logger) : BackgroundService
{
    public Exception? Failure { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var health = await client.Server.GetInfoAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
            LogServerIdentity(logger, health.ServerInfo.Version, health.ServerInfo.Pid);

            var created = await sessions
                .CreateSessionAsync(new SessionCreateRequest
                {
                    Title = "sdk Generic Host stream demo",
                }, cancellationToken: stoppingToken)
                .ConfigureAwait(false);

            LogFollowingSession(logger, created.Session.Id);
            await foreach (var item in experimental
                               .ReadSessionLogAsync(created.Session.Id, new ExperimentalSessionLogRequest
                               {
                                   Follow = true,
                               }, cancellationToken: stoppingToken)
                               .ConfigureAwait(false))
            {
                LogFrame(logger, item.GetType().Name);
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
        Message = "Following session log {SessionId}; stop the host with Ctrl+C")]
    private static partial void LogFollowingSession(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Received typed session log frame {FrameType}")]
    private static partial void LogFrame(ILogger logger, string frameType);
}
