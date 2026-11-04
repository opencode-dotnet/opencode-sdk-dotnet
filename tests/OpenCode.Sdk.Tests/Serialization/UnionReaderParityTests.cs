using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Serialization.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class UnionReaderParityTests
{
    [Test]
    [Arguments("state/streaming/null-required")]
    [Arguments("state/streaming/null-then-valid")]
    [Arguments("tool-content/file/null-required")]
    [Arguments("tool-content/file/null-then-valid")]
    [Arguments("tool-content/text/null-required")]
    [Arguments("tool-content/text/null-then-valid")]
    [Arguments("state/completed/null-optional")]
    [Arguments("state/error/null-optional-metadata")]
    public async Task Read_Should_Match_Metadata_For_The_Eight_Recorded_Gaps(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var state = row.StartsWith("state/", StringComparison.Ordinal);
        var tag = row.Split('/')[1];
        var seed = ToolUnionCaseBuilder.Seeds(state).Single(seed => ToolUnionCaseBuilder.Tag(seed) == tag);
        var key = tag switch { "streaming" => "input", "file" => "uri", "text" => "text", _ => "metadata" };
        var change = row.EndsWith("/null-then-valid", StringComparison.Ordinal) ? "null-first" : "null";
        await CompareAsync(await ToolUnionCaseBuilder.ChangeAsync(seed, key, change), state, row);
    }

    [Test]
    public async Task Read_Should_Match_Metadata_For_Every_Selected_Union_Arm()
    {
        await Assert.That(JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
        foreach (var state in new[] { true, false })
        {
            foreach (var seed in ToolUnionCaseBuilder.Seeds(state))
            {
                foreach (var change in new[] { "maximal", "minimal", "reversed", "escaped", "unknown" })
                {
                    await CompareAsync(await ToolUnionCaseBuilder.ChangeAsync(seed, "", change), state, ToolUnionCaseBuilder.Tag(seed) + "/" + change);
                }
                foreach (var member in seed.EnumerateObject())
                {
                    foreach (var change in new[] { "omit", "null", "wrong", "different-first", "different-last", "null-first", "null-last", "case" })
                    {
                        await CompareAsync(await ToolUnionCaseBuilder.ChangeAsync(seed, member.Name, change), state,
                            ToolUnionCaseBuilder.Tag(seed) + "/" + member.Name + "/" + change);
                    }
                }
            }
        }
    }

    [Test]
    public async Task Read_Should_Refuse_Each_Missing_Required_Member()
    {
        foreach (var state in new[] { true, false })
        {
            foreach (var seed in ToolUnionCaseBuilder.Seeds(state))
            {
                foreach (var key in ToolUnionCaseBuilder.Required(seed))
                {
                    var bytes = await ToolUnionCaseBuilder.ChangeAsync(seed, key, "omit");
                    await CompareAsync(bytes, state, ToolUnionCaseBuilder.Tag(seed) + "/missing/" + key);
                    await Assert.That(Observe(bytes, state, OpenCodeJsonContext.Default).Phase).IsEqualTo("read");
                }
                using var minimal = JsonDocument.Parse(await ToolUnionCaseBuilder.ChangeAsync(seed, "", "minimal"));
                var missing = minimal.RootElement.Clone();
                foreach (var key in ToolUnionCaseBuilder.Required(seed))
                {
                    using var next = JsonDocument.Parse(await ToolUnionCaseBuilder.ChangeAsync(missing, key, "omit"));
                    missing = next.RootElement.Clone();
                }
                await CompareAsync(ToolUnionCaseBuilder.Bytes(missing), state, ToolUnionCaseBuilder.Tag(seed) + "/missing/all");
            }
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Read_Should_Preserve_Every_Maximal_Member(bool state)
    {
        foreach (var seed in ToolUnionCaseBuilder.Seeds(state))
        {
            await CompareAsync(ToolUnionCaseBuilder.Bytes(seed), state,
                (state ? "state/" : "tool-content/") + ToolUnionCaseBuilder.Tag(seed) + "/maximal");
        }
        if (!state)
        {
            var file = (ToolFileContent)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(ToolUnionCaseBuilder.Seeds(false)[0]), OpenCodeJsonContext.Default.IToolContent)!;
            await Assert.That(file.Uri).IsEqualTo("data:λ/fixture");
            await Assert.That(file.Mime).IsEqualTo("text/plain");
            await Assert.That(file.Name).IsEqualTo("file-name");
            var text = (ToolTextContent)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(ToolUnionCaseBuilder.Seeds(false)[1]), OpenCodeJsonContext.Default.IToolContent)!;
            await Assert.That(text.Text).IsEqualTo(ToolUnionCaseBuilder.Seeds(false)[1].GetProperty("text").GetString());
            return;
        }
        var states = ToolUnionCaseBuilder.Seeds(true);
        var completed = JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(states[2]), OpenCodeJsonContext.Default.ISessionMessageToolState);
        await Assert.That(completed).IsTypeOf<SessionMessageToolStateCompleted>();
        var content = ((SessionMessageToolStateCompleted)completed!).Content;
        await Assert.That(content.Select(static item => item.Type)).IsEquivalentTo(["text", "file"], CollectionOrdering.Matching);
        await Assert.That(((ToolFileContent)content[1]).Name).IsEqualTo("nested-file");
        await Assert.That(((ToolTextContent)content[0]).Text).IsEqualTo("nested text λ");
        var streaming = (SessionMessageToolStateStreaming)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(states[0]), OpenCodeJsonContext.Default.ISessionMessageToolState)!;
        await Assert.That(streaming.Input).IsEqualTo(states[0].GetProperty("input").GetString());
        var running = (SessionMessageToolStateRunning)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(states[1]), OpenCodeJsonContext.Default.ISessionMessageToolState)!;
        await Assert.That(running.Input["running"].GetInt32()).IsEqualTo(17);
        await Assert.That(running.Input["nil"].ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(running.Metadata["phase"].GetString()).IsEqualTo("running");
        await Assert.That(((SessionMessageToolStateCompleted)completed).Input["completed"].EnumerateArray().Select(static item => item.ValueKind))
            .IsEquivalentTo([JsonValueKind.Number, JsonValueKind.Null, JsonValueKind.True], CollectionOrdering.Matching);
        var error = (SessionMessageToolStateError)JsonSerializer.Deserialize(ToolUnionCaseBuilder.Bytes(states[3]), OpenCodeJsonContext.Default.ISessionMessageToolState)!;
        await Assert.That(error.Input["error"].GetInt32()).IsEqualTo(23);
        await Assert.That(error.Error.Message).IsEqualTo("nested-message");
        await Assert.That(error.Error.Response!.Body).IsEqualTo("nested-body");
        await Assert.That(error.Content!.Select(static item => item.Type)).IsEquivalentTo(["file", "text"], CollectionOrdering.Matching);
        await Assert.That(error.Metadata!["phase"].GetString()).IsEqualTo("error");
    }

    internal static async Task CompareAsync(byte[] bytes, bool state, string row, Type? root = null, int? split = null)
    {
        var expected = Observe(bytes, state, ToolUnionMetadataOracle<IToolContent>.Create(), split, root: root);
        var actual = Observe(bytes, state, OpenCodeJsonContext.Default, split, root: root);
        await Assert.That(actual.Type).IsEqualTo(expected.Type).Because(row + "/type");
        await Assert.That(actual.Bytes).IsEquivalentTo(expected.Bytes, CollectionOrdering.Matching).Because(row + "/members");
        await Assert.That(actual with { Bytes = expected.Bytes }).IsEqualTo(expected).Because(row + "/type-position-refusal");
    }

    internal static UnionReaderObservation Observe(byte[] bytes, bool state, JsonSerializerContext context, int? split = null, int maxDepth = 0, Type? root = null)
    {
        byte[] framed = [.. "["u8, .. bytes, .. ",731]"u8];
        var sequence = split is { } at ? BufferSegment.Split(framed, at) : new ReadOnlySequence<byte>(framed);
        var reader = new Utf8JsonReader(sequence, new JsonReaderOptions { MaxDepth = maxDepth });
        object? value = null;
        byte[] written = [];
        Exception? failure = null;
        var phase = "success";
        try
        {
            _ = reader.Read();
            _ = reader.Read();
            value = JsonSerializer.Deserialize(ref reader, context.GetTypeInfo(root ?? (state ? typeof(ISessionMessageToolState) : typeof(IToolContent)))!);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
        {
            failure = exception;
            phase = "read";
        }
        if (failure is null)
        {
            try
            {
                written = JsonSerializer.SerializeToUtf8Bytes(value, context.GetTypeInfo(value!.GetType())!);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
            {
                failure = exception;
                phase = "write";
            }
        }
        var error = failure as JsonException;
        var observation = new UnionReaderObservation(phase, value?.GetType().FullName, written, failure?.GetType().FullName,
            error?.Path, failure?.Message, error?.LineNumber, error?.BytePositionInLine,
            reader.BytesConsumed, reader.TokenType, reader.CurrentDepth, reader.TokenStartIndex,
            sequence.Slice(0, reader.Position).Length, null);
        return failure is null && reader.Read() ? observation with { Sentinel = reader.GetInt32() } : observation;
    }
}
