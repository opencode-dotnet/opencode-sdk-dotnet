using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Serialization.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class UnionReaderInputTests
{
    [Test]
    public async Task Read_Should_Match_Metadata_At_Every_Two_Segment_Split()
    {
        var metadata = ToolUnionMetadataOracle<IToolContent>.Create();
        foreach (var state in new[] { true, false })
        {
            foreach (var seed in ToolUnionCaseBuilder.Seeds(state))
            {
                foreach (var shape in new[] { "minimal", "maximal", "escaped", "unknown" })
                {
                    var bytes = await ToolUnionCaseBuilder.ChangeAsync(seed, "", shape);
                    var expected = UnionReaderParityTests.Observe(bytes, state, metadata);
                    await Assert.That(expected.Phase).IsEqualTo("success");
                    for (var split = 1; split < bytes.Length; split++)
                    {
                        var actual = UnionReaderParityTests.Observe(bytes, state, OpenCodeJsonContext.Default, split);
                        await Assert.That(actual.Bytes).IsEquivalentTo(expected.Bytes, CollectionOrdering.Matching)
                            .Because(ToolUnionCaseBuilder.Tag(seed) + "/" + shape + "/split/" + split.ToString(CultureInfo.InvariantCulture));
                        await Assert.That(actual with { Bytes = expected.Bytes }).IsEqualTo(expected);
                        await Assert.That(actual.Sentinel).IsEqualTo(731);
                    }
                }
            }
        }
        var file = ToolUnionCaseBuilder.Bytes(ToolUnionCaseBuilder.Seeds(false)[0]);
        var reader = new Utf8JsonReader(BufferSegment.Split(file, 4));
        _ = reader.Read();
        _ = reader.Read();
        var token = reader.TokenType;
        var segmented = reader.HasValueSequence;
        var name = reader.GetString();
        await Assert.That(token).IsEqualTo(JsonTokenType.PropertyName);
        await Assert.That(segmented).IsTrue();
        await Assert.That(name).IsEqualTo("type");
    }

    [Test]
    public async Task Read_Should_Match_Metadata_For_Short_Read_Streams(CancellationToken cancellationToken)
    {
        var metadata = ToolUnionMetadataOracle<IToolContent>.Create();
        var product = new OpenCodeJsonContext(new JsonSerializerOptions(OpenCodeJsonContext.Default.Options) { DefaultBufferSize = 16 });
        foreach (var state in new[] { true, false })
        {
            var seeds = ToolUnionCaseBuilder.Seeds(state);
            foreach (var seed in seeds)
            {
                var bytes = ToolUnionCaseBuilder.Bytes(seed);
                await CompareStreamsAsync(bytes, state, false, product, metadata, cancellationToken);
                var materialized = await StreamAsync(bytes, state, false, metadata, cancellationToken);
                await Assert.That(materialized.Count).IsEqualTo(2).Because(string.Join('\n', materialized));
                foreach (var end in new[] { 4, Array.IndexOf(bytes, (byte)'\\') + 1, Array.IndexOf(bytes, (byte)0xCE) + 1, bytes.Length - 1 }
                    .Where(static end => end > 0).Distinct())
                {
                    var truncated = bytes.AsSpan(0, end).ToArray();
                    await UnionReaderParityTests.CompareAsync(truncated, state, ToolUnionCaseBuilder.Tag(seed) + "/truncated/" + end.ToString(CultureInfo.InvariantCulture));
                    await CompareStreamsAsync(truncated, state, false, product, metadata, cancellationToken);
                }
            }
            var array = Encoding.UTF8.GetBytes("[" + string.Join(',', seeds.Select(static seed => seed.GetRawText())) + "]");
            await CompareStreamsAsync(array, state, true, product, metadata, cancellationToken);
            var ordered = await StreamAsync(array, state, true, product, cancellationToken);
            await Assert.That(ordered.Where(static (_, index) => index % 2 == 0)).IsEquivalentTo(state
                ? new[] { typeof(SessionMessageToolStateStreaming).FullName, typeof(SessionMessageToolStateRunning).FullName,
                    typeof(SessionMessageToolStateCompleted).FullName, typeof(SessionMessageToolStateError).FullName }
                : [typeof(ToolFileContent).FullName, typeof(ToolTextContent).FullName], CollectionOrdering.Matching);
            using var bad = JsonDocument.Parse(await ToolUnionCaseBuilder.ChangeAsync(seeds[1], ToolUnionCaseBuilder.Required(seeds[1])[0], "wrong"));
            var padded = await ToolUnionCaseBuilder.WithDepthAsync(bad.RootElement, 1);
            var refusal = Encoding.UTF8.GetBytes("[" + seeds[0].GetRawText() + "," + Encoding.UTF8.GetString(padded) + "]");
            var prefix = await StreamAsync(refusal, state, true, metadata, cancellationToken);
            await Assert.That(prefix[0]).IsEqualTo(state ? typeof(SessionMessageToolStateStreaming).FullName : typeof(ToolFileContent).FullName)
                .Because(string.Join('\n', prefix));
            await CompareStreamsAsync(refusal, state, true, product, metadata, cancellationToken);
        }
    }

    [Test]
    public async Task Read_Should_Report_Metadata_Failure_Position_Path_And_Message(CancellationToken cancellationToken)
    {
        using var fixture = JsonDocument.Parse(new FixtureLoader().LoadJson("Serialization.reader-escaped-members.json"));
        var metadata = ToolUnionMetadataOracle<IToolContent>.Create();
        foreach (var row in fixture.RootElement.EnumerateArray())
        {
            var bytes = ToolUnionCaseBuilder.Bytes(row);
            await UnionReaderParityTests.CompareAsync(bytes, true, row.GetRawText());
            for (var split = 1; split < bytes.Length; split++)
            {
                var actual = UnionReaderParityTests.Observe(bytes, true, OpenCodeJsonContext.Default, split);
                var expected = UnionReaderParityTests.Observe(bytes, true, metadata, split);
                await Assert.That(actual.Bytes).IsEquivalentTo(expected.Bytes, CollectionOrdering.Matching);
                await Assert.That(actual with { Bytes = expected.Bytes }).IsEqualTo(expected);
            }
            await CompareStreamsAsync(bytes, true, false, OpenCodeJsonContext.Default, metadata, cancellationToken);
        }
        foreach (var row in new[] { "text-delta", "reasoning-delta", "tool-input-delta", "text-ended" })
        {
            var key = row == "tool-input-delta" ? "delta" : "ordinal";
            var bytes = await PromptEventReaderParityTests.ChangeAsync(PromptEventReaderParityTests.Seed(row), "data", key, "wrong");
            await PromptEventReaderParityTests.CompareAsync(bytes, row + "/nested-failure");
            var expected = UnionReaderParityTests.Observe(bytes, false, metadata, root: typeof(IEvent));
            await Assert.That(expected.Path).IsEqualTo("$.data." + key);
            await Assert.That(await StreamAsync(bytes, false, OpenCodeJsonContext.Default.IEvent, cancellationToken))
                .IsEquivalentTo(await StreamAsync(bytes, false, metadata.IEvent, cancellationToken), CollectionOrdering.Matching);
        }
        var message = await MessageReaderParityTests.DeepFailureAsync();
        await UnionReaderParityTests.CompareAsync(message, false, "assistant/tool/state/failure", typeof(ISessionMessageInfo));
        var failure = UnionReaderParityTests.Observe(message, false, metadata, root: typeof(ISessionMessageInfo));
        await Assert.That(failure.Path).IsEqualTo("$.input");
        await Assert.That(await StreamAsync(message, false, OpenCodeJsonContext.Default.ISessionMessageInfo, cancellationToken))
            .IsEquivalentTo(await StreamAsync(message, false, metadata.ISessionMessageInfo, cancellationToken), CollectionOrdering.Matching);
        var eventBytes = EventReaderParityTests.DeepFailure();
        await PromptEventReaderParityTests.CompareAsync(eventBytes, "event/data/tokens/input/failure");
        await Assert.That(UnionReaderParityTests.Observe(eventBytes, false, metadata, root: typeof(IEvent)).Path).IsEqualTo("$.data.tokens.input");
        await Assert.That(await StreamAsync(eventBytes, false, OpenCodeJsonContext.Default.IEvent, cancellationToken))
            .IsEquivalentTo(await StreamAsync(eventBytes, false, metadata.IEvent, cancellationToken), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Read_Should_Preserve_Depth_Boundaries()
    {
        var metadata = ToolUnionMetadataOracle<IToolContent>.Create();
        foreach (var state in new[] { true, false })
        {
            foreach (var seed in ToolUnionCaseBuilder.Seeds(state))
            {
                foreach (var limit in new[] { 0, 12 })
                {
                    var effective = limit == 0 ? 64 : limit;
                    foreach (var depth in new[] { effective - 3, effective - 2, effective - 1 })
                    {
                        var bytes = await ToolUnionCaseBuilder.WithDepthAsync(seed, depth);
                        var actual = UnionReaderParityTests.Observe(bytes, state, OpenCodeJsonContext.Default, maxDepth: limit);
                        var expected = UnionReaderParityTests.Observe(bytes, state, metadata, maxDepth: limit);
                        await Assert.That(actual.Bytes).IsEquivalentTo(expected.Bytes, CollectionOrdering.Matching);
                        await Assert.That(actual with { Bytes = expected.Bytes }).IsEqualTo(expected);
                        await Assert.That(actual.Phase).IsEqualTo(depth == effective - 1 ? "read" : "success");
                        TestContext.Current!.Output.WriteLine(ToolUnionCaseBuilder.Tag(seed) + "/depth/" + depth.ToString(CultureInfo.InvariantCulture)
                            + ": " + (actual.Phase == "read" ? "serializer read-ahead refuses before union dispatch" : "union scan and member read succeed"));
                    }
                }
            }
        }
    }

    private static async Task CompareStreamsAsync(byte[] bytes, bool state, bool array, OpenCodeJsonContext product, OpenCodeJsonContext metadata, CancellationToken cancellationToken)
    {
        var expected = await StreamAsync(bytes, state, array, metadata, cancellationToken);
        var actual = await StreamAsync(bytes, state, array, product, cancellationToken);
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because(Encoding.UTF8.GetString(bytes));
    }

    private static Task<IReadOnlyList<string>> StreamAsync(byte[] bytes, bool state, bool array, OpenCodeJsonContext context, CancellationToken cancellationToken) => state
        ? StreamAsync(bytes, array, context.ISessionMessageToolState, cancellationToken)
        : StreamAsync(bytes, array, context.IToolContent, cancellationToken);

    internal static async Task<IReadOnlyList<string>> StreamAsync<T>(byte[] bytes, bool array, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        using var stream = ChunkedStream.OfBytes([.. bytes.Select(static value => new[] { value })]);
        var result = new List<string>();
        try
        {
            if (array)
            {
                await foreach (var value in JsonSerializer.DeserializeAsyncEnumerable(stream, info, cancellationToken).WithCancellation(cancellationToken))
                {
                    Add(value);
                }
            }
            else
            {
                Add(await JsonSerializer.DeserializeAsync(stream, info, cancellationToken));
            }
        }
        catch (JsonException exception)
        {
            result.AddRange([exception.GetType().FullName!, exception.Path!, exception.Message]);
            result.Add(exception.LineNumber?.ToString(CultureInfo.InvariantCulture) + "/" + exception.BytePositionInLine?.ToString(CultureInfo.InvariantCulture));
        }
        return result;

        void Add(T? value)
        {
            result.Add(value!.GetType().FullName!);
            result.Add(Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(value, info)));
        }
    }
}
