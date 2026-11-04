using System.Buffers;
using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Serialization.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class MessageReaderParityTests
{
    public static IEnumerable<string> Arms() => ["agent-switched", "assistant", "idle", "location-switched", "model-switched",
        "shell", "skill", "synthetic", "system", "user", "reasoning", "text", "tool"];

    public static IEnumerable<string> Gaps()
    {
        foreach (var arm in Arms())
        {
            var key = arm is "reasoning" or "text" ? "text" : "id";
            yield return arm + "/" + key + "/null-required";
            yield return arm + "/" + key + "/null-then-valid";
            yield return arm + "/" + (Content(arm) ? "native" : "metadata") + "/null-optional";
        }
        foreach (var value in new[] { "nan", "infinity", "negative-infinity", "escaped-nan", "number", "null", "bad" })
        {
            yield return "shell/exit/" + value;
        }
        foreach (var member in new[] { "shell/status", "idle/outcome", "assistant/finish", "tool/executed" })
        {
            yield return member + "/null";
            yield return member + "/wrong";
        }
    }

    [Test]
    [MethodDataSource(nameof(Gaps))]
    public async Task Read_Should_Match_Metadata_For_The_Message_Arm_Gaps(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var parts = row.Split('/');
        var seed = Seed(parts[0]);
        var change = parts[2].StartsWith("null", StringComparison.Ordinal) ? "null" : parts[2];
        if (parts[2] == "null-then-valid")
        {
            change = "null-first";
        }
        byte[] bytes;
        if (parts[1] == "exit")
        {
            bytes = await ReplaceAsync(seed, parts[1], writer => WriteExit(writer, change));
        }
        else
        {
            bytes = await ToolUnionCaseBuilder.ChangeAsync(seed, parts[1], change);
        }
        if (row == "shell/exit/escaped-nan")
        {
            await Assert.That(System.Text.Encoding.UTF8.GetString(bytes)).Contains("\\u004E").Because(row);
        }
        await CompareAsync(bytes, parts[0], row);
        var refused = parts[2] is "null-required" or "null-then-valid" or "wrong" or "bad"
            || (parts[2] == "null" && parts[1] is "status" or "outcome");
        await Assert.That(Observe(bytes, parts[0], ToolUnionMetadataOracle<ISessionMessageInfo>.Create()).Phase)
            .IsEqualTo(refused ? "read" : "success").Because(row);
    }

    [Test]
    [MethodDataSource(nameof(Arms))]
    public async Task Read_Should_Match_Metadata_For_Every_Message_Arm(string row)
    {
        await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
        var seed = Seed(row);
        foreach (var shape in new[] { "minimal", "maximal" })
        {
            var bytes = shape == "minimal" ? await MinimalAsync(seed, Required(row)) : ToolUnionCaseBuilder.Bytes(seed);
            await CompareAsync(bytes, row, row + "/" + shape);
            await Assert.That(Observe(bytes, row, OpenCodeJsonContext.Default).Type).IsEqualTo(ArmType(row).FullName);
            await Assert.That(Observe(bytes, row, OpenCodeJsonContext.Default).Phase).IsEqualTo("success");
        }
    }

    [Test]
    [MethodDataSource(nameof(Arms))]
    public async Task Read_Should_Refuse_Each_Missing_Required_Member(string row)
    {
        var seed = Seed(row);
        foreach (var key in Required(row))
        {
            await RefusalAsync(await ToolUnionCaseBuilder.ChangeAsync(seed, key, "omit"), row, row + "/missing/" + key);
        }
        if (row is "tool" or "reasoning")
        {
            await RefusalAsync(await PromptEventReaderParityTests.ChangeAsync(seed, "time", "created", "omit"), row, row + "/time/missing/created");
        }
    }

    [Test]
    [MethodDataSource(nameof(Arms))]
    public async Task Read_Should_Preserve_Every_Member(string row)
    {
        var seed = Seed(row);
        await CompareAsync(ToolUnionCaseBuilder.Bytes(seed), row, row + "/maximal");
        if (row == "assistant")
        {
            var value = (SessionMessageAssistant)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(seed), OpenCodeJsonContext.Default.ISessionMessageInfo)!;
            await Assert.That(value.Retry!.Attempt).IsEqualTo(83L);
            await Assert.That(value.Retry.At).IsEqualTo(89);
            await Assert.That(value.Retry.Error.Message).IsEqualTo("retry-error");
            await Assert.That(value.Content.Select(static part => part.Type)).IsEquivalentTo(["reasoning", "text", "tool"], CollectionOrdering.Matching);
        }
        if (row == "tool")
        {
            var value = (SessionMessageAssistantTool)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(seed), OpenCodeJsonContext.Default.ISessionMessageAssistantContent)!;
            await Assert.That(value.Time.Created).IsEqualTo(41);
            await Assert.That(value.Time.Ran).IsEqualTo(43);
            await Assert.That(value.Time.Completed).IsEqualTo(47);
        }
        foreach (var key in seed.EnumerateObject().Select(static member => member.Name).Where(static key => key is "cost" or "exit" or "executed" or "time"))
        {
            foreach (var last in new[] { false, true })
            {
                var value = await DifferentAsync(seed.GetProperty(key));
                await CompareAsync(await ReplaceAsync(seed, key, value, last ? "last" : "first"), row, row + "/duplicate/" + key + (last ? "/last" : "/first"));
            }
        }
        if (row is "tool" or "reasoning")
        {
            await CompareAsync(await ReplaceAsync(seed, "time", await MinimalElementAsync(seed.GetProperty("time"), ["created"])), row, row + "/time/minimal");
            await RefusalAsync(await PromptEventReaderParityTests.ChangeAsync(seed, "time", "created", "wrong"), row, row + "/time/wrong");
            foreach (var key in seed.GetProperty("time").EnumerateObject().Select(static member => member.Name))
            {
                foreach (var change in new[] { "different-first", "different-last" })
                {
                    await CompareAsync(await PromptEventReaderParityTests.ChangeAsync(seed, "time", key, change), row, row + "/time/" + key + "/" + change);
                }
            }
        }
        if (row == "reasoning")
        {
            await CompareAsync(await ToolUnionCaseBuilder.ChangeAsync(seed, "time", "null"), row, row + "/time/null");
        }
    }

    [Test]
    [MethodDataSource(nameof(Arms))]
    public async Task Read_Should_Match_Metadata_At_Every_Message_Split(string row)
    {
        foreach (var bytes in new[] { ToolUnionCaseBuilder.Bytes(Seed(row)), await MinimalAsync(Seed(row), Required(row)) })
        {
            for (var split = 1; split < bytes.Length; split++)
            {
                await UnionReaderParityTests.CompareAsync(bytes, false, row + "/split/" + split.ToString(CultureInfo.InvariantCulture), Root(row), split);
            }
        }
        await PromptEventReaderParityTests.AssertSplitPropertyNameAsync(ToolUnionCaseBuilder.Bytes(Seed(row)), Content(row) ? "type" : "id");
    }

    [Test]
    public async Task Read_Should_Read_The_Large_Message_Through_A_Small_Buffer(CancellationToken cancellationToken)
    {
        var bytes = new Performance.Tests.Support.AssistantMessageComposer(Performance.Tests.Support.BenchmarkFixtures.DeepAssistantMessage()).WithContentParts(2400);
        var product = new OpenCodeJsonContext(new JsonSerializerOptions(OpenCodeJsonContext.Default.Options) { DefaultBufferSize = 16 });
        var metadata = ToolUnionMetadataOracle<ISessionMessageInfo>.Create();
        var expected = await UnionReaderInputTests.StreamAsync(bytes, false, metadata.ISessionMessageInfo, cancellationToken);
        await Assert.That(await UnionReaderInputTests.StreamAsync(bytes, false, product.ISessionMessageInfo, cancellationToken))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(expected[0]).IsEqualTo(typeof(SessionMessageAssistant).FullName);
        using var document = JsonDocument.Parse(expected[1]);
        await Assert.That(document.RootElement.GetProperty("content").GetArrayLength()).IsEqualTo(2400);
    }

    internal static JsonElement Seed(string row)
    {
        using var document = JsonDocument.Parse(new FixtureLoader().LoadJson("Serialization.reader-message-arms.json"));
        var values = Content(row) ? document.RootElement.EnumerateArray().Single(static value => value.GetProperty("type").GetString() == "assistant")
            .GetProperty("content").EnumerateArray() : document.RootElement.EnumerateArray();
        return values.Single(value => value.GetProperty("type").GetString() == row).Clone();
    }

    internal static async Task<byte[]> DeepFailureAsync()
    {
        var assistant = Seed("assistant");
        var tool = Seed("tool");
        using var state = JsonDocument.Parse(await ToolUnionCaseBuilder.ChangeAsync(tool.GetProperty("state"), "input", "wrong"));
        using var changed = JsonDocument.Parse(await ReplaceAsync(tool, "state", state.RootElement));
        var content = await ElementAsync(writer =>
        {
            writer.WriteStartArray();
            changed.RootElement.WriteTo(writer);
            writer.WriteEndArray();
        });
        return await ReplaceAsync(assistant, "content", content);
    }

    private static Type ArmType(string row) => row switch
    {
        "agent-switched" => typeof(SessionMessageAgentSelected),
        "assistant" => typeof(SessionMessageAssistant),
        "idle" => typeof(SessionMessageIdle),
        "location-switched" => typeof(SessionMessageLocationSwitched),
        "model-switched" => typeof(SessionMessageModelSelected),
        "shell" => typeof(SessionMessageShell),
        "skill" => typeof(SessionMessageSkill),
        "synthetic" => typeof(SessionMessageSynthetic),
        "system" => typeof(SessionMessageSystem),
        "user" => typeof(SessionMessageUser),
        "reasoning" => typeof(SessionMessageAssistantReasoning),
        "text" => typeof(SessionMessageAssistantText),
        "tool" => typeof(SessionMessageAssistantTool),
        _ => throw new InvalidOperationException(row),
    };

    private static IReadOnlyList<string> Required(string row) => row switch
    {
        "agent-switched" => ["id", "time", "agent"],
        "assistant" => ["id", "time", "agent", "model", "content"],
        "idle" => ["id", "time", "outcome"],
        "location-switched" => ["id", "time", "location"],
        "model-switched" => ["id", "time", "model"],
        "shell" => ["id", "time", "shellID", "command", "status"],
        "skill" => ["id", "time", "skill", "name", "text"],
        "synthetic" or "system" or "user" => ["id", "time", "text"],
        "reasoning" or "text" => ["text"],
        "tool" => ["id", "name", "state", "time"],
        _ => throw new InvalidOperationException(row),
    };

    private static bool Content(string row) => row is "reasoning" or "text" or "tool";
    private static Type Root(string row) => Content(row) ? typeof(ISessionMessageAssistantContent) : typeof(ISessionMessageInfo);
    private static UnionReaderObservation Observe(byte[] bytes, string arm, OpenCodeJsonContext context) =>
        UnionReaderParityTests.Observe(bytes, false, context, root: Root(arm));
    private static Task CompareAsync(byte[] bytes, string arm, string row) => UnionReaderParityTests.CompareAsync(bytes, false, row, Root(arm));
    private static async Task RefusalAsync(byte[] bytes, string arm, string row)
    {
        await CompareAsync(bytes, arm, row);
        await Assert.That(Observe(bytes, arm, OpenCodeJsonContext.Default).Phase).IsEqualTo("read").Because(row);
    }

    private static async Task<byte[]> MinimalAsync(JsonElement seed, IReadOnlyList<string> required) => ToolUnionCaseBuilder.Bytes(await MinimalElementAsync(seed, required));
    private static Task<JsonElement> MinimalElementAsync(JsonElement seed, IReadOnlyList<string> required) => ElementAsync(writer =>
    {
        writer.WriteStartObject();
        foreach (var member in seed.EnumerateObject().Where(member => member.Name == "type" || required.Contains(member.Name, StringComparer.Ordinal)))
        {
            member.WriteTo(writer);
        }
        writer.WriteEndObject();
    });

    private static Task<JsonElement> DifferentAsync(JsonElement value) => ElementAsync(writer =>
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var member in value.EnumerateObject())
            {
                writer.WriteNumber(member.Name, member.Value.GetDouble() + 1000);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.True)
        {
            writer.WriteBooleanValue(false);
        }
        else
        {
            writer.WriteNumberValue(value.GetDouble() + 1000);
        }
    });

    private static void WriteExit(Utf8JsonWriter writer, string change)
    {
        if (change == "null")
        {
            writer.WriteNullValue();
        }
        else if (change == "number")
        {
            writer.WriteNumberValue(123.75);
        }
        else if (change == "escaped-nan")
        {
            writer.WriteRawValue("\"\\u004EaN\"");
        }
        else
        {
            var value = change switch { "nan" => "NaN", "infinity" => "Infinity", "negative-infinity" => "-Infinity", _ => "bad" };
            writer.WriteStringValue(value);
        }
    }

    private static Task<byte[]> ReplaceAsync(JsonElement seed, string key, JsonElement value, string duplicate = "") =>
        ReplaceAsync(seed, key, value.WriteTo, duplicate);

    private static async Task<byte[]> ReplaceAsync(JsonElement seed, string key, Action<Utf8JsonWriter> write, string duplicate = "")
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var member in seed.EnumerateObject())
        {
            if (member.Name != key || duplicate == "last")
            {
                member.WriteTo(writer);
            }
            if (member.Name == key)
            {
                writer.WritePropertyName(key);
                write(writer);
                if (duplicate == "first")
                {
                    member.WriteTo(writer);
                }
            }
        }
        writer.WriteEndObject();
        await writer.FlushAsync();
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task<JsonElement> ElementAsync(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        await writer.FlushAsync();
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
