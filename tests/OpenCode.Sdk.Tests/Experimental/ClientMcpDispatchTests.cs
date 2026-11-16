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
    }

    [Test]
    public async Task DispatchAsync_Should_Admit_B_While_A_Is_Held()
    {
        var fixture = JsonNode.Parse(new FixtureLoader().LoadJson("Experimental.mcp-overlap.json"))!;
        var held = new TaskCompletionSource<ClientMcpToolResult>();
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
                    return name is "A" ? held.Task : Task.FromResult(Result("B"));
                },
            }],
        });
        var dispatch = new ClientMcpDispatch(core, responder, static () => { });
        try
        {
            var a = dispatch.DispatchAsync(Decode(fixture["requests"]![0]!.ToJsonString()));
            var b = dispatch.DispatchAsync(Decode(fixture["requests"]![1]!.ToJsonString()));
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
