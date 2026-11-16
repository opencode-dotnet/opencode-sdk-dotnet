using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenCode.Sdk.Internal;

/// <summary>Answers only the four first-party client-MCP request methods.</summary>
internal sealed class ClientMcpResponder(ClientMcpOptions options)
{
    private readonly ClientMcpTool[] _tools = [.. options.Tools.Select(static tool => tool with { InputSchema = tool.InputSchema?.Clone() })];

    public async Task<byte[]> RespondAsync(ClientMcpMessage message)
    {
        var method = message.Root.GetProperty("method").GetString();
        var parameters = Get(message.Root, "params");
        var reply = method switch
        {
            "initialize" => Initialize(parameters),
            "ping" => new JsonObject { ["result"] = new JsonObject() },
            "tools/list" => new JsonObject { ["result"] = new JsonObject { ["tools"] = new JsonArray([.. _tools.Select(Tool)]) } },
            "tools/call" => await CallAsync(parameters).ConfigureAwait(false),
            _ => Error(-32601, $"Method not found: {method}"),
        };
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Utf8JsonWriter(buffer);
        await using var writerDisposal = writer.ConfigureAwait(false);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        message.Root.GetProperty("id").WriteTo(writer);
        foreach (var property in reply)
        {
            writer.WritePropertyName(property.Key);
            property.Value!.WriteTo(writer);
        }

        writer.WriteEndObject();
        await writer.FlushAsync().ConfigureAwait(false);
        return buffer.WrittenSpan.ToArray();
    }

    internal static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind is JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private JsonObject Initialize(JsonElement parameters)
    {
        var version = Get(parameters, "protocolVersion");
        return new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["protocolVersion"] = version.ValueKind is JsonValueKind.String ? version.GetString() : "2025-11-25",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = options.Name, ["version"] = "1.0.0" },
            },
        };
    }

    private static JsonObject Tool(ClientMcpTool tool)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (tool.InputSchema is { } fields)
        {
            foreach (var property in fields.EnumerateObject())
            {
                schema[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        var declaration = new JsonObject { ["name"] = tool.Name };
        if (tool.Description is not null)
        {
            declaration["description"] = tool.Description;
        }

        declaration["inputSchema"] = schema;
        return declaration;
    }

    private async Task<JsonObject> CallAsync(JsonElement parameters)
    {
        var name = Get(parameters, "name");
        var tool = _tools.FirstOrDefault(tool => name.ValueKind is JsonValueKind.String && string.Equals(tool.Name, name.GetString(), StringComparison.Ordinal));
        if (tool is null)
        {
            return Error(-32602, $"Unknown tool: {Text(name)}");
        }

        var arguments = Get(parameters, "arguments");
        using var empty = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? JsonDocument.Parse("{}") : null;
        var args = empty is null ? arguments.Clone() : empty.RootElement.Clone();
        var session = Get(Get(parameters, "_meta"), "ai.opencode/sessionID");
        var result = await InvokeAsync(args,
            new ClientMcpToolContext(session.ValueKind is JsonValueKind.String ? session.GetString() : null), tool).ConfigureAwait(false);
        var content = new JsonArray();
        foreach (var part in result.Content)
        {
            JsonNode node = part.Type is "text"
                ? new JsonObject { ["type"] = "text", ["text"] = part.Text }
                : new JsonObject { ["type"] = "image", ["data"] = part.Data, ["mimeType"] = part.MimeType };
            content.Add(node);
        }

        var body = new JsonObject { ["content"] = content };
        if (result.IsError is { } error)
        {
            body["isError"] = error;
        }

        return new JsonObject { ["result"] = body };
    }

    private static async Task<ClientMcpToolResult> InvokeAsync(JsonElement args, ClientMcpToolContext context, ClientMcpTool tool)
    {
        try
        {
            return await tool.Handler(args, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new ClientMcpToolResult { Content = [ClientMcpContent.FromText(exception.Message)], IsError = true };
        }
    }

    private static JsonObject Error(int code, string message) =>
        new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "undefined",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "[object Object]",
        JsonValueKind.Array => string.Join(',', value.EnumerateArray().Select(static item => item.ValueKind is JsonValueKind.Null ? string.Empty : Text(item))),
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Unknown JSON value kind."),
    };
}
