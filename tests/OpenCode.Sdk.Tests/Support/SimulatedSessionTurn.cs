using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// One scripted turn against the simulated server: the subscription is attached before the prompt,
/// the drive controller answers the model request with the caller's reply, and the turn is settled
/// from the live event bus rather than by polling.
/// </summary>
internal sealed class SimulatedSessionTurn(
    SimulatedDriveServerFixture server,
    OpenCodeClient client,
    SessionClient session,
    string sessionId)
{
    private const string SimulatedModelId = "sim-model";
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(60);

    public async Task<SessionExecutionSucceeded> CompleteAsync(
        string prompt,
        string reply,
        CancellationToken cancellationToken)
    {
        var reader = new OwnedEventReader(EventWait, CleanupTimeout, cancellationToken);
        var probe = new SessionEventProbe(reader);
        Exception? primaryFailure = null;

        try
        {
            probe.Start(client.Events.SubscribeAsync(cancellationToken: reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);

            _ = await session.PromptAsync(
                new SessionPromptRequest { Text = prompt },
                cancellationToken: cancellationToken);

            var invocation = await DriveAsync(
                () => server.Controller.WaitForRequestAsync(RequestWait),
                "waiting for the model request");
            RequireSimulatedInvocation(invocation);

            await DriveAsync(
                () => server.Controller.ChunkTextAsync(invocation.Id, reply),
                "scripting the reply");
            await DriveAsync(
                () => server.Controller.FinishAsync(invocation.Id),
                "finishing the model turn");

            return await SettleAsync(probe, reply, cancellationToken);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            exception.Data[EventDiagnosticSummary.DataKey] = probe.DiagnosticSummary;
            throw;
        }
        finally
        {
            await reader.CompleteAsync(primaryFailure);
        }
    }

    /// <summary>
    /// The scripted reply's text end, then the success that followed it. Anchoring the second wait
    /// keeps a success that preceded the reply, which belongs to some other execution, from
    /// settling this turn.
    /// </summary>
    private async Task<SessionExecutionSucceeded> SettleAsync(
        SessionEventProbe probe,
        string reply,
        CancellationToken cancellationToken)
    {
        using var barrier = SessionEventProbe.Barrier(cancellationToken);
        var ended = await probe.WaitForAsync<SessionTextEnded>(
            text => text.Data.SessionId == sessionId && text.Data.Text == reply,
            "session.text.ended carrying the scripted reply", barrier.Token);
        return await probe.WaitForAsync<SessionExecutionSucceeded>(
            executed => executed.Data.SessionId == sessionId,
            "session.execution.succeeded for the owned session", ended, barrier.Token);
    }

    private static void RequireSimulatedInvocation(DriveInvocation invocation)
    {
        if (invocation.Model != SimulatedModelId || invocation.Url != SimulationConfigSeed.ChatCompletionsUrl)
        {
            throw new InvalidOperationException(
                $"Expected model '{SimulatedModelId}' at '{SimulationConfigSeed.ChatCompletionsUrl}', but received " +
                $"model '{invocation.Model}' at '{invocation.Url}'.");
        }
    }

    private static async Task<T> DriveAsync<T>(Func<Task<T>> operation, string description)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException($"The drive controller was torn down while {description}.", exception);
        }
    }

    private static async Task DriveAsync(Func<Task> operation, string description)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException($"The drive controller was torn down while {description}.", exception);
        }
    }
}
