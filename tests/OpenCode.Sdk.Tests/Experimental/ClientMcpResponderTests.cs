using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpResponderTests
{
    private readonly FixtureLoader _fixtures = new();

    [Test]
    public async Task RespondAsync_Should_Echo_Any_String_Version_And_Default_Only_Non_Strings()
    {
        foreach (var entry in Cases("initialize"))
        {
            await Assert.That(await ReplyAsync(entry!)).IsEqualTo(entry!["response"]!.ToJsonString());
        }
    }

    [Test]
    public async Task RespondAsync_Should_Answer_Ping_With_An_Empty_Object()
    {
        foreach (var entry in Cases("ping"))
        {
            await Assert.That(await ReplyAsync(entry!)).IsEqualTo(entry!["response"]!.ToJsonString());
        }
    }

    [Test]
    public async Task RespondAsync_Should_Ignore_Jsonrpc_Marker_Differences()
    {
        foreach (var entry in Cases("jsonrpc"))
        {
            await Assert.That(await ReplyAsync(entry!)).IsEqualTo(entry!["response"]!.ToJsonString());
        }
    }

    [Test]
    public async Task RespondAsync_Should_List_Tools_In_Declaration_Order_With_Upstream_Schema_Merge()
    {
        var entry = Cases("tools")[0]!;
        using var document = JsonDocument.Parse(entry["schema"]!.ToJsonString());
        var responder = Responder([
            new ClientMcpTool { Name = "z_lookup", Description = "Lookup", InputSchema = document.RootElement, Handler = static (_, _) => Task.FromResult(Result("z")) },
            new ClientMcpTool { Name = "a_lookup", Handler = static (_, _) => Task.FromResult(Result("a")) },
        ]);
        document.Dispose();
        await Assert.That(await ReplyAsync(entry, responder)).IsEqualTo(entry["response"]!.ToJsonString());
    }

    [Test]
    public async Task RespondAsync_Should_Default_Only_Nullish_Arguments_And_String_Session_Metadata()
    {
        foreach (var entry in Cases("arguments"))
        {
            JsonElement observed = default;
            string? session = null;
            var responder = Responder([new ClientMcpTool
            {
                Name = "lookup",
                Handler = (args, context) =>
                {
                    observed = args;
                    session = context.SessionId;
                    return Task.FromResult(Result("nonce"));
                },
            }]);
            await Assert.That(await ReplyAsync(entry!, responder)).IsEqualTo(entry!["response"]!.ToJsonString());
            await Assert.That(observed.GetRawText()).IsEqualTo(entry["arguments"]!.ToJsonString());
            await Assert.That(session).IsEqualTo(entry["session"]?.GetValue<string>());
        }
    }

    [Test]
    public async Task RespondAsync_Should_Preserve_Ordered_Text_Image_And_Explicit_Error_Flags()
    {
        foreach (var entry in Cases("content"))
        {
            var result = new ClientMcpToolResult
            {
                Content = [ClientMcpContent.FromImage("AQID", "image/png"), ClientMcpContent.FromText("ok")],
                IsError = entry!["flag"]?.GetValue<bool>(),
            };
            var responder = Responder([new ClientMcpTool { Name = "lookup", Handler = (_, _) => Task.FromResult(result) }]);
            await Assert.That(await ReplyAsync(entry, responder)).IsEqualTo(entry["response"]!.ToJsonString());
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespondAsync_Should_Return_Handler_Exceptions_As_Tool_Error_Content(bool faultedTask)
    {
        var responder = Responder([new ClientMcpTool
        {
            Name = "lookup",
            Handler = (_, _) => faultedTask ? Task.FromException<ClientMcpToolResult>(new InvalidOperationException("boom")) : throw new InvalidOperationException("boom"),
        }]);
        var entry = Cases("exception")[0]!;
        await Assert.That(await ReplyAsync(entry, responder)).IsEqualTo(entry["response"]!.ToJsonString());
        var ping = Cases("ping")[0]!;
        await Assert.That(await ReplyAsync(ping, responder)).IsEqualTo(ping["response"]!.ToJsonString());
    }

    [Test]
    public async Task RespondAsync_Should_Use_The_Full_Upstream_Unknown_Tool_Error()
    {
        var invoked = 0;
        var responder = Responder([new ClientMcpTool
        {
            Name = "lookup",
            Handler = (_, _) =>
            {
                invoked++;
                return Task.FromResult(Result("wrong"));
            },
        }]);
        foreach (var entry in Cases("unknown-tool"))
        {
            await Assert.That(await ReplyAsync(entry!, responder)).IsEqualTo(entry!["response"]!.ToJsonString());
        }

        await Assert.That(invoked).IsEqualTo(0);
    }

    [Test]
    public async Task RespondAsync_Should_Return_The_Full_Unknown_Method_Error()
    {
        var entry = Cases("unknown-method")[0]!;
        await Assert.That(await ReplyAsync(entry)).IsEqualTo(entry["response"]!.ToJsonString());
    }

    [Test]
    public async Task RespondAsync_Should_Use_The_First_Duplicate_Tool()
    {
        var responder = Responder([
            new ClientMcpTool { Name = "lookup", Handler = static (_, _) => Task.FromResult(Result("first")) },
            new ClientMcpTool { Name = "lookup", Handler = static (_, _) => Task.FromResult(Result("last")) },
        ]);
        var entry = Cases("duplicate")[0]!;
        await Assert.That(await ReplyAsync(entry, responder)).IsEqualTo(entry["response"]!.ToJsonString());
    }

    private JsonArray Cases(string name) => JsonNode.Parse(_fixtures.LoadJson($"Experimental.mcp-{name}.json"))!.AsArray();

    private static ClientMcpToolResult Result(string text) => new() { Content = [ClientMcpContent.FromText(text)] };

    private static ClientMcpResponder Responder(IReadOnlyList<ClientMcpTool>? tools = null) =>
        new(new ClientMcpOptions { Name = "app", Tools = tools ?? [] });

    private static async Task<string> ReplyAsync(JsonNode entry, ClientMcpResponder? responder = null)
    {
        var bytes = Encoding.UTF8.GetBytes(entry["request"]!.ToJsonString());
        var message = new ClientMcpFrameDecoder().Decode(System.Net.WebSockets.WebSocketMessageType.Text, bytes, bytes.Length);
        using var socket = new ScriptedTerminalWebSocket();
        await using var core = new TerminalSocketCore<ClientMcpMessage>(socket, new ClientMcpFrameDecoder(), PtyClosePolicy.Instance,
            typeof(ClientMcpOptions), new ManualTimeProvider())
        { SendTimeout = Timeout.InfiniteTimeSpan };
        var dispatch = new ClientMcpDispatch(core, responder ?? Responder(), static () => { });
        await dispatch.DispatchAsync(message);
        await dispatch.ObserveResponsesAsync();
        return socket.SentText.Single();
    }
}
