using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Serialization.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class EventReaderParityTests
{
    public static IEnumerable<string> Arms() => ["agent.updated", "command.updated", "integration.updated", "mcp.resources.changed",
        "mcp.status.changed", "model.updated", "models-dev.refreshed", "permission.asked", "permission.replied", "plugin.updated",
        "project.updated", "provider.updated", "reference.updated", "server.connected", "session.created", "session.execution.started",
        "session.execution.succeeded", "session.inbox.delivered", "session.inbox.enqueued", "session.instructions.updated",
        "session.reasoning.ended", "session.reasoning.started", "session.step.ended", "session.step.started", "session.step.streamed",
        "session.text.started", "session.tool.called", "session.tool.input.ended", "session.tool.input.started", "session.tool.success",
        "session.usage.updated", "skill.updated", "vcs.branch.updated", "websearch.updated"];

    public static IEnumerable<string> Turns() => ["turn-1-short-question.jsonl", "turn-2-coding-read-write.jsonl", "turn-3-multi-step.jsonl"];

    public static IEnumerable<string> Gaps() => Arms().SelectMany(static arm => new[] { arm + "/id/null-required",
        arm + "/id/null-then-valid", arm + "/metadata/null-optional", arm + "/location/null-optional" });

    [Test]
    [MethodDataSource(nameof(Gaps))]
    public async Task Read_Should_Match_Metadata_For_The_Event_Arm_Gaps(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var parts = row.Split('/');
        var seed = Seed(parts[0]);
        byte[] bytes;
        if (parts[2] == "null-optional")
        {
            var value = JsonNode.Parse(seed.GetRawText())!.AsObject();
            value[parts[1]] = null;
            bytes = System.Text.Encoding.UTF8.GetBytes(value.ToJsonString());
        }
        else
        {
            bytes = await ToolUnionCaseBuilder.ChangeAsync(seed, parts[1], parts[2] == "null-then-valid" ? "null-first" : "null");
        }
        await PromptEventReaderParityTests.CompareAsync(bytes, row);
        await Assert.That(UnionReaderParityTests.Observe(bytes, false, ToolUnionMetadataOracle<IEvent>.Create(), root: typeof(IEvent)).Phase)
            .IsEqualTo(parts[1] == "id" ? "read" : "success").Because(row);
    }

    [Test]
    [MethodDataSource(nameof(Arms))]
    public async Task Read_Should_Match_Metadata_For_Every_Event_Arm(string row)
    {
        await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
        var seed = Seed(row);
        foreach (var shape in new[] { "maximal", "minimal" })
        {
            var bytes = shape == "minimal" ? await MinimalAsync(seed, Metadata(row)) : ToolUnionCaseBuilder.Bytes(seed);
            await PromptEventReaderParityTests.CompareAsync(bytes, row + "/" + shape);
            var observed = UnionReaderParityTests.Observe(bytes, false, OpenCodeJsonContext.Default, root: typeof(IEvent));
            await Assert.That(observed.Type).IsEqualTo(ToolUnionMetadataOracle<IEvent>.Map[row]!.FullName).Because(row);
            await Assert.That(observed.Phase).IsEqualTo("success").Because(row);
        }
    }

    [Test]
    [MethodDataSource(nameof(Arms))]
    public async Task Read_Should_Refuse_Each_Missing_Required_Member(string row)
    {
        var seed = Seed(row);
        foreach (var property in Metadata(row).Properties.Where(static property => property.IsRequired))
        {
            await RefusalAsync(await ToolUnionCaseBuilder.ChangeAsync(seed, property.Name, "omit"), row + "/missing/" + property.Name);
        }
        if (row == "session.step.ended")
        {
            await RefusalAsync(await PromptEventReaderParityTests.ChangeAsync(seed, "durable", "seq", "omit"), row + "/durable/missing/seq");
        }
    }

    [Test]
    [MethodDataSource(nameof(Arms))]
    public async Task Read_Should_Preserve_Every_Member(string row)
    {
        var seed = Seed(row);
        await PromptEventReaderParityTests.CompareAsync(ToolUnionCaseBuilder.Bytes(seed), row + "/maximal");
        if (row == "session.created")
        {
            var created = (SessionCreated)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(seed), OpenCodeJsonContext.Default.IEvent)!;
            await Assert.That(created.Location!.Directory).IsEqualTo("/created");
            await Assert.That(created.Location.WorkspaceId).IsEqualTo("wrk_created");
            await Assert.That(created.Data.Permissions!.Select(static rule => rule.Resource)).IsEquivalentTo(["*.cs"], CollectionOrdering.Matching);
        }
        if (row == "session.tool.success")
        {
            var success = (SessionToolSuccess)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(seed), OpenCodeJsonContext.Default.IEvent)!;
            await Assert.That(success.Data.Executed).IsTrue();
            await Assert.That(success.Data.Content.Select(static item => item.Type)).IsEquivalentTo(["text", "file"], CollectionOrdering.Matching);
        }
        foreach (var parent in seed.TryGetProperty("durable", out _) ? new[] { "", "durable", "data" } : ["", "data"])
        {
            var value = parent.Length == 0 ? seed : seed.GetProperty(parent);
            foreach (var member in value.EnumerateObject().Where(member => member.Value.ValueKind == JsonValueKind.Number
                         || member.Name == "executed"))
            {
                foreach (var change in new[] { "different-first", "different-last" })
                {
                    var bytes = await PromptEventReaderParityTests.ChangeAsync(seed, parent, member.Name, change);
                    await PromptEventReaderParityTests.CompareAsync(bytes, row + "/duplicate/" + parent + "/" + member.Name + "/" + change);
                }
            }
        }
    }

    public static IEnumerable<string> MemberKinds()
    {
        foreach (var site in new[] { "permission.asked/resources", "permission.asked/save", "project.updated/sandboxes",
                     "session.step.ended/files", "session.tool.called/executed", "session.tool.success/executed", "session.instructions.updated/delta" })
        {
            yield return site + "/null";
            yield return site + "/wrong";
            if (!site.EndsWith("executed", StringComparison.Ordinal))
            {
                yield return site + "/empty";
            }
        }
        foreach (var arm in new[] { "session.created", "server.connected" })
        {
            yield return arm + "/location/omit";
            yield return arm + "/location/wrong";
            yield return arm + "/location/null";
        }
    }

    [Test]
    [MethodDataSource(nameof(MemberKinds))]
    public async Task Read_Should_Match_Metadata_For_New_Event_Member_Kinds(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var parts = row.Split('/');
        var seed = Seed(parts[0]);
        byte[] bytes;
        if (parts[1] == "location")
        {
            bytes = parts[2] == "null" ? await ToolUnionCaseBuilder.ChangeAsync(seed, "location", "null")
                : await PromptEventReaderParityTests.ChangeAsync(seed, "location", "directory", parts[2]);
        }
        else if (parts[2] == "empty")
        {
            bytes = await ReplaceDataAsync(seed, parts[1], writer =>
            {
                if (parts[1] == "delta")
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }
            });
        }
        else
        {
            bytes = await PromptEventReaderParityTests.ChangeAsync(seed, "data", parts[1], parts[2]);
        }
        await PromptEventReaderParityTests.CompareAsync(bytes, row);
        var optional = parts[1] is "save" or "files" or "location";
        var refuses = parts[2] == "wrong" || (parts[2] == "null" && !optional) || parts[2] == "omit";
        await Assert.That(UnionReaderParityTests.Observe(bytes, false, OpenCodeJsonContext.Default, root: typeof(IEvent)).Phase)
            .IsEqualTo(refuses ? "read" : "success").Because(row);
    }

    [Test]
    [Arguments("session.created")]
    [Arguments("session.step.ended")]
    public async Task Read_Should_Match_Metadata_For_Shared_Event_Records(string row)
    {
        var bytes = ToolUnionCaseBuilder.Bytes(Seed(row));
        var actual = JsonSerializer.Deserialize(bytes, OpenCodeJsonContext.Default.ISessionLogItem)!;
        var info = Metadata(row);
        var expected = JsonSerializer.Deserialize(bytes, info)!;
        await Assert.That(actual.GetType()).IsEqualTo(expected.GetType());
        await Assert.That(JsonSerializer.SerializeToUtf8Bytes(actual, OpenCodeJsonContext.Default.ISessionLogItem))
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(expected, info), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Turns))]
    public async Task Read_Should_Read_Captured_Turns_Through_A_Small_Buffer(string row, CancellationToken cancellationToken)
    {
        var product = new OpenCodeJsonContext(new JsonSerializerOptions(OpenCodeJsonContext.Default.Options) { DefaultBufferSize = 16 });
        var metadata = ToolUnionMetadataOracle<IEvent>.Create();
        foreach (var bytes in BenchmarkFixtures.JsonLines(row))
        {
            var expected = await UnionReaderInputTests.StreamAsync(bytes, false, metadata.IEvent, cancellationToken);
            await Assert.That(await UnionReaderInputTests.StreamAsync(bytes, false, product.IEvent, cancellationToken))
                .IsEquivalentTo(expected, CollectionOrdering.Matching).Because(row);
            await Assert.That(expected[0]).IsEqualTo(JsonSerializer.Deserialize(bytes, metadata.IEvent)!.GetType().FullName);
        }
    }

    internal static JsonElement Seed(string tag)
    {
        using var document = JsonDocument.Parse(new FixtureLoader().LoadJson("Serialization.reader-event-arms.json"));
        var fixture = document.RootElement.EnumerateArray().FirstOrDefault(value => value.GetProperty("type").GetString() == tag);
        if (fixture.ValueKind != JsonValueKind.Undefined)
        {
            return fixture.Clone();
        }
        foreach (var bytes in Turns().SelectMany(BenchmarkFixtures.JsonLines))
        {
            using var frame = JsonDocument.Parse(bytes);
            if (frame.RootElement.GetProperty("type").GetString() == tag)
            {
                return frame.RootElement.Clone();
            }
        }
        throw new InvalidOperationException("No real or maximal event: " + tag);
    }

    internal static byte[] DeepFailure()
    {
        var value = JsonNode.Parse(Seed("session.step.ended").GetRawText())!.AsObject();
        value["data"]!["tokens"]!["input"] = "wrong-token";
        return System.Text.Encoding.UTF8.GetBytes(value.ToJsonString());
    }

    private static JsonTypeInfo Metadata(string tag) => ToolUnionMetadataOracle<IEvent>.Create().GetTypeInfo(ToolUnionMetadataOracle<IEvent>.Map[tag]!)!;

    private static async Task RefusalAsync(byte[] bytes, string row)
    {
        await PromptEventReaderParityTests.CompareAsync(bytes, row);
        await Assert.That(UnionReaderParityTests.Observe(bytes, false, OpenCodeJsonContext.Default, root: typeof(IEvent)).Phase).IsEqualTo("read").Because(row);
    }

    private static async Task<byte[]> MinimalAsync(JsonElement seed, JsonTypeInfo info)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        WriteMinimal(writer, seed, info);
        await writer.FlushAsync();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMinimal(Utf8JsonWriter writer, JsonElement seed, JsonTypeInfo info)
    {
        writer.WriteStartObject();
        foreach (var member in seed.EnumerateObject())
        {
            var property = info.Properties.SingleOrDefault(property => property.Name == member.Name);
            if (member.Name != "type" && property?.IsRequired != true)
            {
                continue;
            }
            writer.WritePropertyName(member.Name);
            var nested = property is null ? null : info.Options.GetTypeInfo(property.PropertyType);
            if (member.Value.ValueKind == JsonValueKind.Object && nested?.Kind == JsonTypeInfoKind.Object)
            {
                WriteMinimal(writer, member.Value, nested);
            }
            else
            {
                member.Value.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    private static Task<byte[]> ReplaceDataAsync(JsonElement seed, string key, Action<Utf8JsonWriter> write) =>
        ToolUnionCaseBuilder.ReplaceAsync(seed, "data", writer => WriteData(writer, seed.GetProperty("data"), key, write));

    private static void WriteData(Utf8JsonWriter writer, JsonElement data, string key, Action<Utf8JsonWriter> write)
    {
        writer.WriteStartObject();
        foreach (var child in data.EnumerateObject())
        {
            writer.WritePropertyName(child.Name);
            if (child.Name == key)
            {
                write(writer);
            }
            else
            {
                child.Value.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }
}
