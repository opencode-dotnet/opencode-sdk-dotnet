using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Serialization.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class PromptEventReaderParityTests
{
    [Test]
    [Arguments("text-delta/null-required")]
    [Arguments("text-delta/null-then-valid")]
    [Arguments("reasoning-delta/null-required")]
    [Arguments("reasoning-delta/null-then-valid")]
    [Arguments("tool-input-delta/null-required")]
    [Arguments("tool-input-delta/null-then-valid")]
    [Arguments("text-ended/null-required")]
    [Arguments("text-ended/null-then-valid")]
    [Arguments("text-delta/null-optional")]
    [Arguments("reasoning-delta/null-optional")]
    [Arguments("tool-input-delta/null-optional")]
    [Arguments("text-ended/null-optional")]
    [Arguments("text-ended/data/native-null")]
    public async Task Read_Should_Match_Metadata_For_The_Prompt_Arm_Gaps(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var seed = Seed(row.Split('/')[0]);
        var nested = row.EndsWith("native-null", StringComparison.Ordinal);
        var optional = row.EndsWith("null-optional", StringComparison.Ordinal);
        var change = row.EndsWith("null-then-valid", StringComparison.Ordinal) ? "null-first" : "null";
        var key = optional ? "metadata" : "id";
        var bytes = await ChangeAsync(seed, nested ? "data" : "", nested ? "native" : key, change);
        await CompareAsync(bytes, row);
        await Assert.That(Observe(bytes, ToolUnionMetadataOracle<IEvent>.Create()).Phase)
            .IsEqualTo(nested || optional ? "success" : "read").Because(row);
    }

    [Test]
    [Arguments("text-delta")]
    [Arguments("reasoning-delta")]
    [Arguments("tool-input-delta")]
    [Arguments("text-ended")]
    public async Task Read_Should_Match_Metadata_For_Every_Prompt_Arm(string row)
    {
        await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
        var seed = Seed(row);
        await Assert.That(JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(seed), OpenCodeJsonContext.Default.IEvent)!.GetType())
            .IsEqualTo(row switch
            {
                "text-delta" => typeof(SessionTextDelta),
                "reasoning-delta" => typeof(SessionReasoningDelta),
                "tool-input-delta" => typeof(SessionToolInputDelta),
                _ => typeof(SessionTextEnded),
            });
        foreach (var shape in new[] { "maximal", "minimal", "reversed", "escaped", "unknown" })
        {
            await CompareAsync(await ToolUnionCaseBuilder.ChangeAsync(seed, "", shape), row + "/" + shape);
        }
        foreach (var member in seed.EnumerateObject())
        {
            foreach (var change in new[] { "omit", "null", "wrong", "null-first", "null-last", "case" })
            {
                await CompareAsync(await ChangeAsync(seed, "", member.Name, change), row + "/" + member.Name + "/" + change);
            }
        }
    }

    [Test]
    [Arguments("text-delta")]
    [Arguments("reasoning-delta")]
    [Arguments("tool-input-delta")]
    [Arguments("text-ended")]
    public async Task Read_Should_Refuse_Each_Missing_Required_Member(string row)
    {
        var seed = Seed(row);
        foreach (var key in ToolUnionCaseBuilder.Required(seed))
        {
            await RefusalAsync(await ChangeAsync(seed, "", key, "omit"), row + "/missing/" + key);
        }
        foreach (var parent in Nested(seed))
        {
            foreach (var member in seed.GetProperty(parent).EnumerateObject().Where(static member => member.Name != "native"))
            {
                await RefusalAsync(await ChangeAsync(seed, parent, member.Name, "omit"), row + "/" + parent + "/missing/" + member.Name);
            }
        }
    }

    [Test]
    [Arguments("text-delta")]
    [Arguments("reasoning-delta")]
    [Arguments("tool-input-delta")]
    [Arguments("text-ended")]
    public async Task Read_Should_Preserve_Every_Nested_Member(string row)
    {
        var seed = Seed(row);
        await CompareAsync(ToolUnionCaseBuilder.Bytes(seed), row + "/maximal");
        foreach (var parent in Nested(seed))
        {
            foreach (var member in seed.GetProperty(parent).EnumerateObject())
            {
                foreach (var change in new[] { "different-first", "different-last", "null", "null-first", "wrong" })
                {
                    await CompareAsync(await ChangeAsync(seed, parent, member.Name, change), row + "/" + parent + "/" + member.Name + "/" + change);
                }
            }
        }
        foreach (var change in new[] { "different-first", "different-last" })
        {
            await CompareAsync(await ChangeAsync(seed, "", "created", change), row + "/created/" + change);
            await CompareAsync(await ChangeAsync(seed, "", "id", change), row + "/id/" + change);
        }
        var value = JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(seed), OpenCodeJsonContext.Default.IEvent)!;
        var data = JsonSerializer.SerializeToElement(value, OpenCodeJsonContext.Default.IEvent).GetProperty("data");
        await Assert.That(data.GetProperty("sessionID").GetString()).IsEqualTo("ses_" + Suffix(row));
        await Assert.That(data.GetProperty("assistantMessageID").GetString()).IsEqualTo("msg_" + Suffix(row));
        await Assert.That(data.GetProperty(row == "text-ended" ? "text" : "delta").GetString()).IsEqualTo(row switch
        {
            "text-delta" => "text λ \"fragment\"",
            "reasoning-delta" => "reason λ fragment",
            "tool-input-delta" => "tool λ fragment",
            _ => "ended λ text",
        });
        if (row == "tool-input-delta")
        {
            await Assert.That(data.GetProperty("id").GetString()).IsEqualTo("tool_input");
        }
        else
        {
            await Assert.That(data.GetProperty("ordinal").GetInt64()).IsEqualTo(row switch { "text-delta" => 23L, "reasoning-delta" => 31L, _ => 53L });
        }
        if (value is SessionTextEnded ended)
        {
            await CompareAsync(await ChangeAsync(seed, "data", "native", "omit"), row + "/data/minimal");
            await Assert.That(ended.Durable.AggregateId).IsEqualTo("agg_ended");
            await Assert.That(ended.Durable.Seq).IsEqualTo(43L);
            await Assert.That(ended.Durable.Version).IsEqualTo(47.5);
            await Assert.That(ended.Data.Native!["state"].EnumerateArray().Select(static item => item.ValueKind))
                .IsEquivalentTo([JsonValueKind.Number, JsonValueKind.Null, JsonValueKind.True], CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("text-delta")]
    [Arguments("reasoning-delta")]
    [Arguments("tool-input-delta")]
    [Arguments("text-ended")]
    public async Task Read_Should_Match_Metadata_At_Every_Prompt_Split(string row)
    {
        var seed = Seed(row);
        var malformed = await ChangeAsync(seed, "data", row == "tool-input-delta" ? "delta" : "ordinal", "wrong");
        var original = ToolUnionCaseBuilder.Bytes(seed);
        var truncated = original.AsSpan(0, original.Length - 1).ToArray();
        foreach (var bytes in new[] { ToolUnionCaseBuilder.Bytes(seed), await ToolUnionCaseBuilder.ChangeAsync(seed, "", "minimal"),
                     await ToolUnionCaseBuilder.ChangeAsync(seed, "", "escaped"), malformed, truncated })
        {
            var expected = Observe(bytes, ToolUnionMetadataOracle<IEvent>.Create());
            for (var split = 1; split < bytes.Length; split++)
            {
                await CompareAsync(bytes, row + "/split/" + split.ToString(CultureInfo.InvariantCulture), split);
            }
            await Assert.That(expected.Phase).IsEqualTo(ReferenceEquals(bytes, malformed) || ReferenceEquals(bytes, truncated) ? "read" : "success");
        }
        await AssertSplitPropertyNameAsync(original, "id");
    }

    [Test]
    [Arguments("text-delta")]
    [Arguments("reasoning-delta")]
    [Arguments("tool-input-delta")]
    [Arguments("text-ended")]
    public async Task Read_Should_Preserve_Nested_Depth_Boundaries(string row)
    {
        var seed = Seed(row);
        foreach (var limit in new[] { 0, 12 })
        {
            var effective = limit == 0 ? 64 : limit;
            foreach (var arrays in new[] { effective - 4, effective - 3, effective - 2 })
            {
                using var data = JsonDocument.Parse(await ToolUnionCaseBuilder.WithDepthAsync(seed.GetProperty("data"), arrays));
                var bytes = await ReplaceAsync(seed, "data", data.RootElement);
                await CompareAsync(bytes, row + "/data/depth/" + arrays.ToString(CultureInfo.InvariantCulture), maxDepth: limit);
                await Assert.That(Observe(bytes, OpenCodeJsonContext.Default, maxDepth: limit).Phase)
                    .IsEqualTo(arrays == effective - 2 ? "read" : "success");
            }
        }
    }

    [Test]
    public async Task Read_Should_Read_Prompt_Seed_Frames_Through_Streams_And_The_Subscription_Adapter(CancellationToken cancellationToken)
    {
        var metadata = ToolUnionMetadataOracle<IEvent>.Create();
        var product = new OpenCodeJsonContext(new JsonSerializerOptions(OpenCodeJsonContext.Default.Options) { DefaultBufferSize = 16 });
        for (var index = 0; index < 16 * 65; index++)
        {
            var id = "evt_bench" + (index + 1).ToString("D20", CultureInfo.InvariantCulture);
            var delta = index % 65;
            var frame = delta == 64 ? Performance.Tests.Support.BenchmarkFixtures.PromptTextEndedBody(id)
                : (delta % 4) switch
                {
                    2 => Performance.Tests.Support.BenchmarkFixtures.PromptReasoningDeltaBody(id),
                    3 => Performance.Tests.Support.BenchmarkFixtures.PromptToolInputDeltaBody(id),
                    _ => Performance.Tests.Support.BenchmarkFixtures.PromptTextDeltaBody(id),
                };
            var bytes = Encoding.UTF8.GetBytes(frame);
            await Assert.That(await UnionReaderInputTests.StreamAsync(bytes, false, product.IEvent, cancellationToken))
                .IsEquivalentTo(await UnionReaderInputTests.StreamAsync(bytes, false, metadata.IEvent, cancellationToken), CollectionOrdering.Matching);
            var actual = JsonSerializer.Deserialize(bytes, EventSubscribeResponseStreamAdapter.Instance.PayloadTypeInfo);
            await Assert.That(actual!.GetType().FullName).IsEqualTo(Observe(bytes, metadata).Type);
            await Assert.That(JsonSerializer.SerializeToUtf8Bytes(actual, product.IEvent))
                .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize(bytes, metadata.IEvent), metadata.IEvent), CollectionOrdering.Matching);
        }
        foreach (var row in new[] { "text-delta", "reasoning-delta", "tool-input-delta", "text-ended" })
        {
            var original = ToolUnionCaseBuilder.Bytes(Seed(row));
            var wrong = await ChangeAsync(Seed(row), "data", "sessionID", "wrong");
            foreach (var end in new[] { Array.IndexOf(original, (byte)'\\') + 1, Array.IndexOf(original, (byte)0xCE) + 1, original.Length - 1 }.Where(static end => end > 0))
            {
                await Assert.That(await UnionReaderInputTests.StreamAsync(original.AsSpan(0, end).ToArray(), false, product.IEvent, cancellationToken))
                    .IsEquivalentTo(await UnionReaderInputTests.StreamAsync(original.AsSpan(0, end).ToArray(), false, metadata.IEvent, cancellationToken), CollectionOrdering.Matching);
            }
            await Assert.That(await UnionReaderInputTests.StreamAsync(wrong, false, product.IEvent, cancellationToken))
                .IsEquivalentTo(await UnionReaderInputTests.StreamAsync(wrong, false, metadata.IEvent, cancellationToken), CollectionOrdering.Matching);
        }
    }

    internal static JsonElement Seed(string row)
    {
        using var document = JsonDocument.Parse(new FixtureLoader().LoadJson("Serialization.reader-prompt-events.json"));
        return document.RootElement.EnumerateArray().Single(seed => seed.GetProperty("id").GetString() == "evt_" + Suffix(row)).Clone();
    }

    internal static async Task AssertSplitPropertyNameAsync(byte[] bytes, string expected)
    {
        var reader = new Utf8JsonReader(BufferSegment.Split(bytes, Array.IndexOf(bytes, (byte)'"') + 2));
        _ = reader.Read();
        _ = reader.Read();
        var segmented = reader.HasValueSequence;
        var name = reader.GetString();
        await Assert.That(segmented).IsTrue();
        await Assert.That(name).IsEqualTo(expected);
    }

    internal static Task<byte[]> ChangeAsync(JsonElement seed, string parent, string member, string change) => parent.Length == 0
        ? ToolUnionCaseBuilder.ChangeAsync(seed, member, change) : ChangeNestedAsync(seed, parent, member, change);

    private static async Task<byte[]> ChangeNestedAsync(JsonElement seed, string parent, string member, string change)
    {
        using var replacement = JsonDocument.Parse(await ToolUnionCaseBuilder.ChangeAsync(seed.GetProperty(parent), member, change));
        return await ReplaceAsync(seed, parent, replacement.RootElement);
    }

    private static async Task<byte[]> ReplaceAsync(JsonElement seed, string key, JsonElement replacement)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var member in seed.EnumerateObject())
        {
            writer.WritePropertyName(member.Name);
            (member.Name == key ? replacement : member.Value).WriteTo(writer);
        }
        writer.WriteEndObject();
        await writer.FlushAsync();
        return buffer.WrittenSpan.ToArray();
    }

    private static IEnumerable<string> Nested(JsonElement seed) => seed.TryGetProperty("durable", out _) ? ["data", "durable"] : ["data"];
    private static string Suffix(string row) => row switch { "text-delta" => "text", "reasoning-delta" => "reason", "tool-input-delta" => "tool", _ => "ended" };

    private static UnionReaderObservation Observe(byte[] bytes, OpenCodeJsonContext context, int? split = null, int maxDepth = 0) =>
        UnionReaderParityTests.Observe(bytes, false, context, split, maxDepth, typeof(IEvent));

    private static async Task RefusalAsync(byte[] bytes, string row)
    {
        await CompareAsync(bytes, row);
        await Assert.That(Observe(bytes, OpenCodeJsonContext.Default).Phase).IsEqualTo("read").Because(row);
    }

    internal static async Task CompareAsync(byte[] bytes, string row, int? split = null, int maxDepth = 0)
    {
        var expected = Observe(bytes, ToolUnionMetadataOracle<IEvent>.Create(), split, maxDepth);
        var actual = Observe(bytes, OpenCodeJsonContext.Default, split, maxDepth);
        await Assert.That(actual.Type).IsEqualTo(expected.Type).Because(row + "/type");
        await Assert.That(actual.Bytes).IsEquivalentTo(expected.Bytes, CollectionOrdering.Matching).Because(row + "/members");
        await Assert.That(actual with { Bytes = expected.Bytes }).IsEqualTo(expected).Because(row + "/position-refusal");
        if (expected.Phase == "success")
        {
            await Assert.That(actual.Sentinel).IsEqualTo(731).Because(row);
        }
    }
}
