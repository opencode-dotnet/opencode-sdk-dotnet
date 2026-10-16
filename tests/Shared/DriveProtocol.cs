using System.Text.Json;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Composes the drive backend's JSON-RPC requests (protocol source of truth:
/// packages/protocol/src/simulation.ts at the pin — Handshake.Params, the model Item union,
/// ToolRegistration/ToolAttachParams, ToolUpdateParams, ToolFinishParams, ToolFailParams;
/// llm.attach/llm.pending carry no params). Every request carries a numeric id: id-less
/// requests get no response. Supplied <see cref="JsonElement"/> values are written with
/// <see cref="JsonElement.WriteTo"/>, never as encoded strings.
/// </summary>
internal static class DriveProtocol
{
    public static byte[] Handshake(long id) =>
        Compose(id, "simulation.handshake", static writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteStartObject("client");
            writer.WriteString("name", "opencode-sdk-dotnet");
            writer.WriteString("version", "tests");
            writer.WriteEndObject();
            writer.WriteString("expectedRole", "backend");
            writer.WriteStartArray("offeredVersions");
            writer.WriteNumberValue(1);
            writer.WriteEndArray();
            writer.WriteStartArray("requiredCapabilities");
            writer.WriteStringValue("llm.attach");
            writer.WriteStringValue("llm.request");
            writer.WriteStringValue("llm.chunk");
            writer.WriteStringValue("llm.finish");
            writer.WriteStringValue("tool.attach");
            writer.WriteStringValue("tool.update");
            writer.WriteStringValue("tool.finish");
            writer.WriteStringValue("tool.fail");
            writer.WriteStringValue("tool.invocation");
            writer.WriteStringValue("tool.cancel");
            writer.WriteEndArray();
            writer.WriteStartArray("optionalCapabilities");
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    public static byte[] Attach(long id) => Compose(id, "llm.attach", writeParams: null);

    public static byte[] ChunkText(long id, string invocationId, IReadOnlyList<string> deltas) =>
        Compose(id, "llm.chunk", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("id", invocationId);
            writer.WriteStartArray("items");
            foreach (var delta in deltas)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "textDelta");
                writer.WriteString("text", delta);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    /// <summary>The canonical single <c>toolCall</c> model item: the simulator lowers it to a real streamed tool call.</summary>
    public static byte[] ChunkToolCall(long id, string invocationId, string callId, string name, JsonElement input) =>
        Compose(id, "llm.chunk", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("id", invocationId);
            writer.WriteStartArray("items");
            writer.WriteStartObject();
            writer.WriteString("type", "toolCall");
            writer.WriteNumber("index", 0);
            writer.WriteString("id", callId);
            writer.WriteString("name", name);
            writer.WritePropertyName("input");
            input.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    public static byte[] ToolAttach(long id, IReadOnlyList<DriveToolRegistration> tools) =>
        Compose(id, "tool.attach", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteStartArray("tools");
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("inputSchema");
                tool.InputSchema.WriteTo(writer);
                if (tool.OutputSchema is { } output)
                {
                    writer.WritePropertyName("outputSchema");
                    output.WriteTo(writer);
                }

                // Direct tools only: no namespace, and codemode off so the model calls the tool
                // by its registered name.
                writer.WriteStartObject("options");
                writer.WriteBoolean("codemode", false);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    public static byte[] ToolUpdate(long id, string toolId, int sequence, JsonElement update) =>
        Compose(id, "tool.update", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("id", toolId);
            writer.WriteNumber("sequence", sequence);
            writer.WritePropertyName("update");
            update.WriteTo(writer);
            writer.WriteEndObject();
        });

    public static byte[] ToolFinish(long id, string toolId, JsonElement structured, string text) =>
        Compose(id, "tool.finish", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("id", toolId);
            writer.WriteStartObject("output");
            writer.WritePropertyName("structured");
            structured.WriteTo(writer);
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    public static byte[] ToolFail(long id, string toolId, string message) =>
        Compose(id, "tool.fail", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("id", toolId);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });

    public static byte[] Finish(long id, string invocationId, string reason) =>
        Compose(id, "llm.finish", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("id", invocationId);
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
        });

    public static byte[] Disconnect(long id, string invocationId) =>
        Compose(id, "llm.disconnect", writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("id", invocationId);
            writer.WriteEndObject();
        });

    public static byte[] Pending(long id) => Compose(id, "llm.pending", writeParams: null);

    private static byte[] Compose(long id, string method, Action<Utf8JsonWriter>? writeParams)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writeParams?.Invoke(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
