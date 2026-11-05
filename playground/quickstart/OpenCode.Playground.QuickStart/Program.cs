using OpenCode.Playground.QuickStart;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
try
{
    // A private server on your normal opencode data. It stops when this block ends.
    await using var server = await OpenCodeServer.StartAsync();
    using var client = server.CreateClient();
    Console.WriteLine($"server: {server.Endpoint}");

    var model = await new FreeModel(client).FindAsync(deadline.Token);
    if (model is null)
    {
        await Console.Error.WriteLineAsync("No free model within 30 seconds: the server may still be starting, so try again, or see the README to use your own provider.");
        return 1;
    }

    Console.WriteLine($"model: {model.ProviderId}/{model.Id} (the provider sees what you send)");
    var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
    {
        Title = "OpenCode playground: QuickStart",
        Model = new ModelRef { Id = model.Id, ProviderId = model.ProviderId },
    }, cancellationToken: deadline.Token);

    // Open the bus first: an event published before you listen is gone.
    await using var events = client.Events.SubscribeAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
    await events.MoveNextAsync();

    var session = client.Sessions.GetSessionClient(created.Session.Id);
    await session.PromptAsync(new SessionPromptRequest { Text = "In one sentence, what is a typed SDK?" }, cancellationToken: deadline.Token);

    while (await events.MoveNextAsync())
    {
        switch (events.Current)
        {
            case SessionTextDelta delta when delta.Data.SessionId == created.Session.Id:
                Console.Write(delta.Data.Delta);
                break;
            case SessionRetryScheduled retry when retry.Data.SessionId == created.Session.Id:
                await Console.Error.WriteLineAsync($"The provider will be retried: {retry.Data.Error.Message}");
                break;
            case SessionExecutionSucceeded done when done.Data.SessionId == created.Session.Id:
                Console.WriteLine();
                return 0;
            case SessionExecutionFailed failed when failed.Data.SessionId == created.Session.Id:
                await Console.Error.WriteLineAsync($"The model turn failed: {failed.Data.Error.Type}: {failed.Data.Error.Message}");
                return 1;
            case SessionExecutionInterrupted stopped when stopped.Data.SessionId == created.Session.Id:
                await Console.Error.WriteLineAsync($"The model turn was interrupted: {stopped.Data.Reason}");
                return 1;
        }
    }

    return 1;
}
catch (Exception failure) when (failure is OpenCodeException or OperationCanceledException)
{
    await Console.Error.WriteLineAsync($"QuickStart stopped: {failure.Message} On a first run, check the prerequisites: https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/playground/README.md#prerequisites");
    return 1;
}
