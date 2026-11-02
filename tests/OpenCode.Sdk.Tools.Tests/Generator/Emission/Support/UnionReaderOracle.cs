using System.Buffers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal sealed class UnionReaderOracle(JsonSerializerContext product, JsonSerializerContext metadata)
{
    public JsonSerializerContext Product { get; } = product;
    public JsonSerializerContext Metadata { get; } = metadata;

    public static async Task<UnionReaderOracle> CreateAsync(EmitPlan plan, IReadOnlyList<GeneratedSource>? productSources = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var product = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(productSources ?? SourceEmitter.Emit(plan));
        var metadata = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(MetadataUnionOracleEmitter.Emit(plan));
        return new(Context(product), Context(metadata));
    }

    internal static JsonSerializerContext Context(Assembly assembly) =>
        (JsonSerializerContext)(assembly.GetType("OpenCode.Sdk.Internal.OpenCodeJsonContext", throwOnError: true)!
            .GetProperty("Default")!.GetValue(null) ?? throw new InvalidOperationException("Missing generated context."));

    public async Task CompareAsync(RecordCorpusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var root in new[] { row.RootType, row.UnionType }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var expected = Observe(Metadata, root, row.Json);
            if (row.IsSeed)
            {
                await Assert.That(expected.Phase).IsEqualTo("success").Because(row.Name + "/seed/" + root);
            }
            await AssertEquivalentAsync(Observe(Product, root, row.Json), expected, row.Name + "/" + root);
        }
    }

    public static UnionReaderObservation Observe(JsonSerializerContext context, string root, byte[] json)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(json);
        var sequence = new ReadOnlySequence<byte>([.. "["u8, .. json, .. ",731]"u8]);
        var reader = new Utf8JsonReader(sequence);
        var phase = "read";
        object? value = null;
        byte[] bytes = [];
        Exception? failure = null;
        int? sentinel = null;
        try
        {
            _ = reader.Read();
            _ = reader.Read();
            // Named context accessors also cover collection and internal envelope roots.
            var type = context.GetType().GetProperty(root)?.GetValue(context) is System.Text.Json.Serialization.Metadata.JsonTypeInfo info
                ? info.Type : context.GetType().Assembly.GetType("OpenCode.Sdk.Models." + root, throwOnError: true)!;
            value = JsonSerializer.Deserialize(ref reader, context.GetTypeInfo(type)!);
            phase = "write";
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, context.GetTypeInfo(value?.GetType() ?? type)!);
            phase = "success";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
        {
            failure = exception;
        }

        var consumed = reader.BytesConsumed;
        var token = reader.TokenType;
        var depth = reader.CurrentDepth;
        var start = reader.TokenStartIndex;
        var offset = sequence.Slice(0, reader.Position).Length;
        if (failure is null && reader.Read())
        {
            sentinel = reader.GetInt32();
        }

        var error = failure as JsonException;
        return new(phase, value?.GetType().FullName, bytes, failure?.GetType().FullName, error?.Path, failure?.Message,
            error?.LineNumber, error?.BytePositionInLine, consumed, token, depth, start, offset, sentinel);
    }

    public static async Task AssertEquivalentAsync(UnionReaderObservation actual, UnionReaderObservation expected, string row)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        await Assert.That(actual.Phase).IsEqualTo(expected.Phase).Because(row);
        await Assert.That(actual.Type).IsEqualTo(expected.Type).Because(row + "/type");
        await Assert.That(actual.Bytes).IsEquivalentTo(expected.Bytes, CollectionOrdering.Matching).Because(row + "/members");
        await Assert.That(actual with { Bytes = expected.Bytes }).IsEqualTo(expected).Because(row + "/position-or-refusal");
        if (expected.Phase == "success")
        {
            await Assert.That(actual.Sentinel).IsEqualTo(731).Because(row + "/next-value");
        }
    }
}
