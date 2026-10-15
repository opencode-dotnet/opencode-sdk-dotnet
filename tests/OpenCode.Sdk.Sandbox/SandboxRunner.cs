using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Sandbox;

/// <summary>
/// Runs every mode that talks to a server <see cref="SandboxConnection"/> reaches (the
/// <c>--standalone</c> demo starts its own, so <c>Program.cs</c> sends it to
/// <see cref="StandaloneServerWalkthrough"/> instead). Split out of <c>Program.cs</c>'s top-level
/// statements to keep each mode's dispatch under the repository's method-size and complexity
/// gates.
/// </summary>
internal static class SandboxRunner
{
    public static async Task<int> RunAsync(SandboxArguments arguments)
    {
        await using var connection = await SandboxConnection.OpenAsync(arguments.Endpoint).ConfigureAwait(false);
        Console.WriteLine($"server:  {connection.Description}");

        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddOpenCode(connection.Configure);

        if (arguments.Mode is SandboxMode.Stream)
        {
            _ = builder.Services.AddSingleton<SessionLogWorker>();
            _ = builder.Services.AddHostedService(static provider => provider.GetRequiredService<SessionLogWorker>());
        }
        else if (arguments.Mode is SandboxMode.Events)
        {
            _ = builder.Services.AddSingleton<EventBusWorker>();
            _ = builder.Services.AddHostedService(static provider => provider.GetRequiredService<EventBusWorker>());
        }

        using var host = builder.Build();

        if (arguments.Mode is SandboxMode.Stream)
        {
            return await RunStreamModeAsync(host).ConfigureAwait(false);
        }

        if (arguments.Mode is SandboxMode.Events)
        {
            return await RunEventModeAsync(host).ConfigureAwait(false);
        }

        var client = host.Services.GetRequiredService<OpenCodeClient>();

        var health = await client.Server.GetInfoAsync().ConfigureAwait(false);
        Console.WriteLine($"health:  status={health.Status} version={health.ServerInfo.Version} pid={health.ServerInfo.Pid}");

        return arguments.Mode is SandboxMode.Paginate
            ? await RunPaginationModeAsync(client, arguments.SessionId!).ConfigureAwait(false)
            : await RunBreadthWalkthroughAsync(host, client).ConfigureAwait(false);
    }

    private static async Task<int> RunStreamModeAsync(IHost host)
    {
        var worker = host.Services.GetRequiredService<SessionLogWorker>();
        await host.RunAsync().ConfigureAwait(false);
        return worker.Failure is null ? 0 : 1;
    }

    private static async Task<int> RunEventModeAsync(IHost host)
    {
        var worker = host.Services.GetRequiredService<EventBusWorker>();
        await host.RunAsync().ConfigureAwait(false);
        return worker.Failure is null ? 0 : 1;
    }

    private static async Task<int> RunPaginationModeAsync(OpenCodeClient client, string sessionId)
    {
        var count = 0;
        var sessionClient = client.Sessions.GetSessionClient(sessionId);

        var listRequest = new SessionMessageListRequest
        {
            Limit = 1,
            Order = ListOrder.Ascending,
        };
        var messageStream = sessionClient.EnumerateMessagesAsync(listRequest, CancellationToken.None);

        await foreach (var message in messageStream.WithCancellation(CancellationToken.None))
        {
            count++;
            Console.WriteLine($"page-item-{count}: {message.GetType().Name}/{message.Type}");
            if (count is 2)
            {
                break;
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"enumerated={count}"));
        return count is 2 ? 0 : 1;
    }

    /// <summary>
    /// The default mode: health, session create/list/get, message list, and the remaining
    /// walkthrough legs (mechanism actions, session actions, envelope completion, PTY), all
    /// through one client.
    /// </summary>
    private static async Task<int> RunBreadthWalkthroughAsync(IHost host, OpenCodeClient client)
    {
        // Sub-clients resolve directly from the container as well.
        var sessionsClient = host.Services.GetRequiredService<SessionsClient>();

        var createRequest = new SessionCreateRequest
        {
            Title = "sdk breadth demo",
        };
        var created = await sessionsClient.CreateSessionAsync(createRequest).ConfigureAwait(false);

        Console.WriteLine($"create:  status={created.Status} id={created.Session.Id} title={created.Session.Title}");

        var sessionListRequest = new SessionListRequest
        {
            Limit = 3,
            Order = ListOrder.Descending,
        };
        var page = await sessionsClient.ListSessionsAsync(sessionListRequest).ConfigureAwait(false);

        Console.WriteLine($"list:    status={page.Status} sessions={page.Sessions.Count} cursor.next={page.Cursor.Next ?? "<none>"}");

        foreach (var session in page.Sessions)
        {
            Console.WriteLine($"         {session.Id}  {session.Title}");
        }

        var handle = sessionsClient.GetSessionClient(created.Session.Id);
        var fetched = await handle.GetAsync().ConfigureAwait(false);

        Console.WriteLine($"get:     status={fetched.Status} id={fetched.Session.Id} directory={fetched.Session.Location.Directory}");

        var messageListRequest = new SessionMessageListRequest
        {
            Limit = 5,
        };
        var messages = await handle.ListMessagesAsync(messageListRequest).ConfigureAwait(false);

        Console.WriteLine($"messages: status={messages.Status} count={messages.Messages.Count} cursor.next={messages.Cursor.Next ?? "<none>"}");

        foreach (var message in messages.Messages)
        {
            Console.WriteLine($"         {message.GetType().Name}");
        }

        await SessionActionsWalkthrough.RunAsync(client, handle, created.Session.Id).ConfigureAwait(false);
        await MechanismActionsWalkthrough.RunAsync(client, handle, created.Session.Id).ConfigureAwait(false);
        await EnvelopeCompletionWalkthrough.RunAsync(client, handle).ConfigureAwait(false);
        await PtySessionWalkthrough.RunAsync(client).ConfigureAwait(false);
        await PersistentPtyWalkthrough.RunAsync(client, created.Session.Id).ConfigureAwait(false);

        return 0;
    }
}
