using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpDispatchTests
{
    [Test]
    public async Task DispatchAsync_Should_Answer_Ping_After_An_Unreadable_Method()
    {
        var entry = JsonNode.Parse(new FixtureLoader().LoadJson("Experimental.mcp-unknown-method.json"))![1]!;
        using var socket = new ScriptedTerminalWebSocket();
        await using var core = Core(socket);
        var dispatch = new ClientMcpDispatch(core, new ClientMcpResponder(new ClientMcpOptions { Name = "app", Tools = [] }), static () => { });
        await dispatch.DispatchAsync(Decode(entry["raw"]!.GetValue<string>()));
        await dispatch.ObserveResponsesAsync();
        await dispatch.DispatchAsync(Support.ClientMcpScenario.Message("ping"));
        await dispatch.ObserveResponsesAsync();
        await Assert.That(socket.SentText).IsEquivalentTo([
            entry["response"]!.ToJsonString(),
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["result"] = new JsonObject() }.ToJsonString(),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DispatchAsync_Should_Ignore_Non_Requests_Without_Reply()
    {
        var fixture = JsonNode.Parse(new FixtureLoader().LoadJson("Experimental.mcp-ignored.json"))!;
        using var socket = new ScriptedTerminalWebSocket();
        await using var core = Core(socket);
        var resets = 0;
        var dispatch = new ClientMcpDispatch(core, new ClientMcpResponder(new ClientMcpOptions { Name = "app", Tools = [] }), () => resets++);
        foreach (var entry in fixture["messages"]!.AsArray())
        {
            await dispatch.DispatchAsync(Decode(entry!["raw"]?.GetValue<string>() ?? entry["value"]!.ToJsonString(),
                entry["binary"]?.GetValue<bool>() is true ? WebSocketMessageType.Binary : WebSocketMessageType.Text));
        }

        await dispatch.ObserveResponsesAsync();
        await Assert.That(socket.SentText).IsEquivalentTo(fixture["responses"]!.AsArray().Select(static node => node!.ToJsonString()), CollectionOrdering.Matching);
        await dispatch.DispatchAsync(Decode(fixture["initialized"]!.ToJsonString()));
        await dispatch.ObserveResponsesAsync();
        await Assert.That(resets).IsEqualTo(1);
        await Assert.That(socket.SentText).IsEquivalentTo(fixture["responses"]!.AsArray().Select(static node => node!.ToJsonString())
            .Append(fixture["initializedResponse"]!.ToJsonString()), CollectionOrdering.Matching);
        var outstanding = (IReadOnlyCollection<Task>)typeof(ClientMcpDispatch).GetField("_responses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(dispatch)!;
        await Assert.That(outstanding.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DispatchAsync_Should_Answer_Ping_While_A_Handler_Blocks_Synchronously()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var socket = new ScriptedTerminalWebSocket();
        await using var core = Core(socket);
        var responder = new ClientMcpResponder(Support.ClientMcpScenario.Options((_, _) =>
        {
            entered.SetResult(Environment.CurrentManagedThreadId);
            release.Wait();
            return Task.FromResult(Result("done"));
        }));
        var dispatch = new ClientMcpDispatch(core, responder, static () => { });
        var readerThread = 0;
        var reader = Task.Factory.StartNew(async () =>
        {
            readerThread = Environment.CurrentManagedThreadId;
            await dispatch.DispatchAsync(Support.ClientMcpScenario.Message("tools/call"));
            await dispatch.DispatchAsync(Support.ClientMcpScenario.Message("ping"));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            var handlerThread = await entered.Task.WaitAsync(HangGuard.Bound);
            await Assert.That(handlerThread).IsNotEqualTo(readerThread);
            await socket.SendEntered.WaitAsync(HangGuard.Bound);
            await Assert.That(socket.SentText).IsEquivalentTo([
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["result"] = new JsonObject() }.ToJsonString(),
            ], CollectionOrdering.Matching);
        }
        finally
        {
            release.Set();
            await reader;
            await dispatch.ObserveResponsesAsync();
        }
    }

    [Test]
    public async Task DispatchAsync_Should_Admit_B_While_A_Is_Held()
    {
        var fixture = JsonNode.Parse(new FixtureLoader().LoadJson("Experimental.mcp-overlap.json"))!;
        var held = new TaskCompletionSource<ClientMcpToolResult>();
        var aStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var core = Core(socket);
        var responder = new ClientMcpResponder(new ClientMcpOptions
        {
            Name = "app",
            Tools = [new ClientMcpTool
            {
                Name = "lookup",
                Handler = (args, _) =>
                {
                    var name = args.GetProperty("name").GetString()!;
                    started.Add(name);
                    (name is "A" ? aStarted : bStarted).SetResult(true);
                    return name is "A" ? held.Task : Task.FromResult(Result("B"));
                },
            }],
        });
        var dispatch = new ClientMcpDispatch(core, responder, static () => { });
        try
        {
            var a = dispatch.DispatchAsync(Decode(fixture["requests"]![0]!.ToJsonString()));
            await aStarted.Task.WaitAsync(HangGuard.Bound);
            var b = dispatch.DispatchAsync(Decode(fixture["requests"]![1]!.ToJsonString()));
            await bStarted.Task.WaitAsync(HangGuard.Bound);
            await socket.SendEntered.WaitAsync(HangGuard.Bound);
            await Assert.That(a.IsCompleted).IsTrue();
            await Assert.That(b.IsCompleted).IsTrue();
            await Assert.That(started).IsEquivalentTo(["A", "B"], CollectionOrdering.Matching);
            await Task.WhenAll(a, b);
            await Assert.That(socket.SentText).IsEquivalentTo([fixture["responses"]![0]!.ToJsonString()], CollectionOrdering.Matching);
            held.SetResult(Result("A"));
            await Assert.That(socket.MaxConcurrentSends).IsEqualTo(1);
            socket.ReleaseSends();
            await dispatch.ObserveResponsesAsync();
            await Assert.That(socket.SentText).IsEquivalentTo(fixture["responses"]!.AsArray().Select(static node => node!.ToJsonString()), CollectionOrdering.Matching);
        }
        finally
        {
            _ = held.TrySetResult(Result("A"));
            socket.ReleaseSends();
            await dispatch.ObserveResponsesAsync();
        }
    }

    private static ClientMcpToolResult Result(string text) => new() { Content = [ClientMcpContent.FromText(text)] };

    private static TerminalSocketCore<ClientMcpMessage> Core(ScriptedTerminalWebSocket socket) =>
        new(socket, new ClientMcpFrameDecoder(), PtyClosePolicy.Instance, typeof(ClientMcpOptions), new ManualTimeProvider())
        { SendTimeout = Timeout.InfiniteTimeSpan };

    private static ClientMcpMessage Decode(string text, WebSocketMessageType type = WebSocketMessageType.Text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new ClientMcpFrameDecoder().Decode(type, bytes, bytes.Length);
    }
}
