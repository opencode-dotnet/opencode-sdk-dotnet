using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenCode.Sdk.Internal;

/// <summary>Answers only the four first-party client-MCP request methods.</summary>
internal sealed class ClientMcpResponder(ClientMcpOptions options)
{
    private readonly ClientMcpTool[] _tools = [.. options.Tools.Select(static tool => tool with { InputSchema = tool.InputSchema?.Clone() })];

    public async Task<byte[]> RespondAsync(ClientMcpMessage message)
    {
        var method = ReadString(message.Root.GetProperty("method"));
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
            // Every response envelope member is constructed as a non-null object.
            property.Value!.WriteTo(writer);
        }

        writer.WriteEndObject();
        await writer.FlushAsync().ConfigureAwait(false);
        return buffer.WrittenSpan.ToArray();
    }

    internal static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind is JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    internal static string? ReadString(JsonElement value)
    {
        try
        {
            return value.ValueKind is JsonValueKind.String ? value.GetString() : null;
        }
        catch (InvalidOperationException)
        {
            // Unpaired wire surrogates are not representable strings; use the non-string path.
            return null;
        }
    }

    private JsonObject Initialize(JsonElement parameters)
    {
        var version = Get(parameters, "protocolVersion");
        return new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["protocolVersion"] = ReadString(version) ?? "2025-11-25",
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
        var tool = _tools.FirstOrDefault(tool => string.Equals(tool.Name, ReadString(name), StringComparison.Ordinal));
        if (tool is null)
        {
            return Error(-32602, $"Unknown tool: {Text(name)}");
        }

        var arguments = Get(parameters, "arguments");
        using var empty = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? JsonDocument.Parse("{}") : null;
        var args = empty is null ? arguments.Clone() : empty.RootElement.Clone();
        var session = Get(Get(parameters, "_meta"), "ai.opencode/sessionID");
        var result = await InvokeAsync(args,
            new ClientMcpToolContext(ReadString(session)), tool).ConfigureAwait(false);
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
            var result = await tool.Handler(args, context).ConfigureAwait(false);
            if (result is null || result.Content is null || result.Content.Any(static part => part is null))
            {
                throw new InvalidOperationException("Client MCP handlers must return non-null results, content and content elements.");
            }

            return result;
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
        JsonValueKind.String => ReadString(value) ?? "undefined",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.TryGetDouble(out var number) && !double.IsInfinity(number) ? NumberText(number) : "undefined",
        _ => throw new InvalidOperationException("Unknown JSON value kind."),
    };

    private static string NumberText(double number)
    {
        if (number is 0)
        {
            return "0";
        }

        var precision = 1;
        string text;
        do
        {
            text = number.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            precision++;
        } while (!Utf8Parser.TryParse(Encoding.UTF8.GetBytes(text), out double roundTrip, out _)
            || BitConverter.DoubleToInt64Bits(roundTrip) != BitConverter.DoubleToInt64Bits(number));

        var parts = text.Split(['E', 'e'], StringSplitOptions.None);
        var sign = number < 0 ? "-" : string.Empty;
        var coefficient = parts[0].TrimStart('-');
        var dot = coefficient.IndexOf('.', StringComparison.Ordinal);
        var digits = string.Concat(coefficient.Split('.'));
        var point = (dot < 0 ? coefficient.Length : dot) + (parts.Length is 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
        var significant = digits.TrimStart('0');
        point -= digits.Length - significant.Length;
        var magnitude = Math.Abs(number);
        if (magnitude >= 1e-6 && magnitude < 1e21)
        {
            return sign + (point switch
            {
                <= 0 => "0." + new string('0', -point) + significant,
                _ when point >= significant.Length => significant.PadRight(point, '0'),
                _ => significant.Insert(point, "."),
            });
        }

        var exponent = point - 1;
        return sign + significant[0] + (significant.Length > 1 ? "." + significant[1..] : string.Empty)
            + "e" + (exponent >= 0 ? "+" : string.Empty) + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
