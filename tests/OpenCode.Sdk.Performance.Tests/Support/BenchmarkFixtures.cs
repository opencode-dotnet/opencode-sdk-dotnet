using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>Loads and frames the canned wire payloads the benchmarks replay.</summary>
internal static class BenchmarkFixtures
{
    private const string TextFragment =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The live bus opener shared by idle and prompt connections.</summary>
    public static byte[] ConnectedEventBody() => "{\"id\":\"evt_connected\",\"type\":\"server.connected\",\"data\":{}}"u8.ToArray();

    public static string PromptTextDeltaBody(string id) =>
        "{\"id\":\"" + id + "\",\"created\":1,\"type\":\"session.text.delta\",\"data\":{\"sessionID\":\"ses_bench00000000000000000001\",\"assistantMessageID\":\"msg_bench00000000000000000001\",\"ordinal\":0,\"delta\":\""
        + TextFragment + "\"}}";

    public static string PromptReasoningDeltaBody(string id) =>
        "{\"id\":\"" + id + "\",\"created\":1,\"type\":\"session.reasoning.delta\",\"data\":{\"sessionID\":\"ses_bench00000000000000000001\",\"assistantMessageID\":\"msg_bench00000000000000000001\",\"ordinal\":1,\"delta\":\"Thinking\"}}";

    public static string PromptToolInputDeltaBody(string id) =>
        "{\"id\":\"" + id + "\",\"created\":1,\"type\":\"session.tool.input.delta\",\"data\":{\"sessionID\":\"ses_bench00000000000000000001\",\"assistantMessageID\":\"msg_bench00000000000000000001\",\"id\":\"tool_bench\",\"delta\":\"x\"}}";

    public static string PromptTextEndedBody(string id) =>
        "{\"id\":\"" + id + "\",\"created\":1,\"type\":\"session.text.ended\",\"durable\":{\"aggregateID\":\"ses_bench00000000000000000001\",\"seq\":1,\"version\":1},\"data\":{\"sessionID\":\"ses_bench00000000000000000001\",\"assistantMessageID\":\"msg_bench00000000000000000001\",\"ordinal\":0,\"text\":\""
        + string.Concat(Enumerable.Repeat(TextFragment, 32)) + "\"}}";

    /// <summary>
    /// A deep assistant message — reasoning + text + two tool parts across four union
    /// levels — in the compact single-line form the wire carries. Indentation would put
    /// bytes through every measurement that no server ever sends.
    /// </summary>
    public static byte[] DeepAssistantMessage() => Read("deep-assistant-message.json");

    /// <summary>The bare server info payload the live server returns.</summary>
    public static byte[] InfoBody() => "{\"urls\":[\"http://localhost:4096\"],\"version\":\"0.0.0-bench\",\"pid\":42,\"paths\":{\"tmp\":\"/tmp/opencode\"}}"u8.ToArray();

    /// <summary>
    /// A small framing-only payload, the size the live feed carries most of; the parser
    /// benchmarks never materialize it, so it is not shaped as a generated event.
    /// </summary>
    public static byte[] SessionIdleBody() =>
        "{\"type\":\"session.idle\",\"properties\":{\"sessionID\":\"ses_bench\"}}"u8.ToArray();

    /// <summary>The small live-bus event an idle session emits, in the shape the generated event union reads.</summary>
    public static byte[] SessionIdleEventBody() =>
        "{\"id\":\"evt_bench\",\"created\":1,\"type\":\"session.idle\",\"data\":{\"sessionID\":\"ses_bench\"}}"u8.ToArray();

    /// <summary>A session record as the create and get operations return it.</summary>
    public static byte[] SessionInfoBody() =>
        Encoding.UTF8.GetBytes(
            "{\"id\":\"ses_bench0000000000000000001\",\"projectID\":\"prj_bench\",\"title\":\"Fix the build\",\"cost\":0.42,"
            + "\"tokens\":{\"input\":10,\"output\":20,\"reasoning\":0,\"cache\":{\"read\":1,\"write\":2}},"
            + "\"time\":{\"created\":1755100000,\"updated\":1755100050},"
            + "\"location\":{\"directory\":\"/repo\",\"workspaceID\":\"wrk_bench\"}}");

    /// <summary>The declared 404 error body a missing session answers with.</summary>
    public static byte[] SessionNotFoundErrorBody() =>
        "{\"_tag\":\"SessionNotFoundError\",\"sessionID\":\"ses_bench0000000000000000001\",\"message\":\"gone\"}"u8.ToArray();

    /// <summary>The small watermark a session log emits after replay reaches its captured sequence.</summary>
    public static byte[] SessionLogSyncedBody() =>
        "{\"type\":\"log.synced\",\"aggregateID\":\"ses_bench\",\"seq\":2}"u8.ToArray();

    /// <summary>A durable session-log event with a large, schema-valid title.</summary>
    public static byte[] LargeSessionCreatedBody(int titleCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(titleCharacters, 1);
        var title = new string('x', titleCharacters);
        return Encoding.UTF8.GetBytes(
            "{\"id\":\"evt_bench\",\"created\":1,\"type\":\"session.created\","
            + "\"durable\":{\"aggregateID\":\"ses_bench\",\"seq\":1,\"version\":1},"
            + "\"location\":{\"directory\":\"/repo\"},"
            + "\"data\":{\"sessionID\":\"ses_bench\",\"projectID\":\"prj_bench\","
            + "\"location\":{\"directory\":\"/repo\"},\"slug\":\"benchmark\",\"title\":\""
            + title
            + "\",\"agent\":\"build\",\"version\":\"1\"}}");
    }

    /// <summary>The small durable event a session deletion writes.</summary>
    public static byte[] SessionDeletedBody() =>
        Encoding.UTF8.GetBytes("{\"id\":\"evt_bench\",\"created\":2,\"type\":\"session.deleted\","
         + "\"durable\":{\"aggregateID\":\"ses_bench\",\"seq\":2,\"version\":2},"
         + "\"location\":{\"directory\":\"/repo\"},\"data\":{\"sessionID\":\"ses_bench\"}}");

    /// <summary>
    /// A structured durable event: a tool success whose content list alternates text and file
    /// parts, so a large frame is nested union payload rather than one long string.
    /// </summary>
    public static byte[] SessionToolSuccessBody(int contentParts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contentParts, 1);
        var builder = new StringBuilder(
            "{\"id\":\"evt_bench\",\"created\":3,\"type\":\"session.tool.success\","
            + "\"durable\":{\"aggregateID\":\"ses_bench\",\"seq\":3,\"version\":1},"
            + "\"location\":{\"directory\":\"/repo\"},"
            + "\"data\":{\"sessionID\":\"ses_bench\",\"assistantMessageID\":\"msg_bench00000000000000000001\","
            + "\"id\":\"tool_bench_shell_1\",\"content\":[");
        for (var index = 0; index < contentParts; index++)
        {
            if (index > 0)
            {
                _ = builder.Append(',');
            }

            var ordinal = index.ToString(CultureInfo.InvariantCulture);
            var passed = (index + 200).ToString(CultureInfo.InvariantCulture);
            _ = (index % 2) is 0
                ? builder.Append("{\"type\":\"text\",\"text\":\"Passed! - Failed: 0, Passed: ").Append(passed)
                    .Append(", Skipped: 0, Total: ").Append(passed).Append(", Duration: 4.9s\"}")
                : builder.Append("{\"type\":\"file\",\"mime\":\"text/plain\",\"uri\":\"https://bench.invalid/artifacts/run-")
                    .Append(ordinal).Append(".txt\",\"name\":\"run-").Append(ordinal).Append(".txt\"}");
        }

        _ = builder.Append("],\"metadata\":{\"exitCode\":0,\"durationMs\":5211},\"executed\":true}}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// The active-sessions dictionary payload: <paramref name="count"/> entries keyed by session
    /// ID, each carrying the one declared <c>"running"</c> type tag, in the object shape
    /// <c>session.active</c> returns (not the ordered array a cursor-list page carries).
    /// </summary>
    public static byte[] SessionActiveDictionary(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var builder = new StringBuilder("{");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                _ = builder.Append(',');
            }

            _ = builder.Append("\"ses_bench").Append((index + 1).ToString("D20", CultureInfo.InvariantCulture))
                .Append("\":{\"type\":\"running\"}");
        }

        _ = builder.Append('}');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Wraps a payload in the <c>{"data": ...}</c> success envelope.</summary>
    public static byte[] DataEnvelope(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return [.. "{\"data\":"u8, .. payload, .. "}"u8];
    }

    /// <summary>Wraps payloads in the cursor-list envelope as one page whose cursor points onward.</summary>
    public static byte[] CursorListEnvelope(IReadOnlyList<byte[]> payloads, string? nextCursor = "cur_bench_next")
    {
        ArgumentNullException.ThrowIfNull(payloads);
        using var buffer = new MemoryStream();
        buffer.Write("{\"data\":["u8);
        for (var index = 0; index < payloads.Count; index++)
        {
            if (index > 0)
            {
                buffer.WriteByte((byte)',');
            }

            buffer.Write(payloads[index]);
        }

        buffer.Write("],\"cursor\":"u8);
        buffer.Write(nextCursor is null ? "{}"u8 : Encoding.UTF8.GetBytes($"{{\"next\":\"{nextCursor}\"}}"));
        buffer.WriteByte((byte)'}');
        return buffer.ToArray();
    }

    /// <summary>Frames one payload as a run of server-sent events, the shape a live stream delivers.</summary>
    public static byte[] EventStream(byte[] payload, int frames)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        return EventStream([.. Enumerable.Repeat(payload, frames)]);
    }

    /// <summary>Frames each payload as one server-sent event, in order.</summary>
    public static byte[] EventStream(IReadOnlyList<byte[]> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        using var buffer = new MemoryStream();
        foreach (var payload in payloads)
        {
            buffer.Write("data: "u8);
            buffer.Write(payload);
            buffer.Write("\n\n"u8);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Frames one payload per event but splits it across several <c>data:</c> lines, the
    /// grammar's multi-line form, so the reader's line join is measured against the one-line wire.
    /// </summary>
    public static byte[] MultiLineEventStream(byte[] payload, int frames, int linesPerFrame)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(linesPerFrame, 2);
        var lineLength = Math.Max(1, payload.Length / linesPerFrame);
        using var buffer = new MemoryStream();
        for (var frame = 0; frame < frames; frame++)
        {
            for (var offset = 0; offset < payload.Length; offset += lineLength)
            {
                buffer.Write("data: "u8);
                buffer.Write(payload.AsSpan(offset, Math.Min(lineLength, payload.Length - offset)));
                buffer.WriteByte((byte)'\n');
            }

            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    private static byte[] Read(string name)
    {
        var stream = typeof(BenchmarkFixtures).Assembly
                         .GetManifestResourceStream($"OpenCode.Sdk.Performance.Tests.Fixtures.{name}")
                     ?? throw new InvalidOperationException($"Embedded fixture '{name}' is missing.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return WireShaped(reader.ReadBytes(checked((int)stream.Length)), name);
    }

    /// <summary>
    /// Holds every fixture to the shape a server actually sends. Indentation puts whitespace
    /// bytes through each measurement and frames as one line of content in an event stream,
    /// so the numbers would describe the fixture rather than the code under test. The file's
    /// own trailing newline is framing, not payload, and comes off.
    /// </summary>
    private static byte[] WireShaped(byte[] payload, string name)
    {
        var end = payload.Length;
        while (end > 0 && payload[end - 1] is (byte)'\n' or (byte)'\r')
        {
            end--;
        }

        var trimmed = payload.AsSpan(0, end).ToArray();
        return Array.IndexOf(trimmed, (byte)'\n') < 0
            ? trimmed
            : throw new InvalidOperationException(
                $"Fixture '{name}' is not wire-shaped: the payload a server sends occupies one line.");
    }
}
