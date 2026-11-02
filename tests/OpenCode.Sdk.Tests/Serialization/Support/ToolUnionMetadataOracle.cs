using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Serialization.Support;

/// <summary>Overrides both converted unions, including the content members of tool states.</summary>
internal sealed class ToolUnionMetadataOracle<TUnion> : MetadataUnionJsonConverter<TUnion>
{
    private static readonly bool State = typeof(TUnion) == typeof(ISessionMessageToolState);
    protected override string Concept => State ? "SessionMessageToolState" : "ToolContent";
    protected override string Marker => State ? "status" : "type";
    protected override UnionTagTable<Type?> Types => Table;
    public static IReadOnlyDictionary<string, Type?> Map { get; } = State
        ? new Dictionary<string, Type?>(StringComparer.Ordinal)
        {
            ["completed"] = typeof(SessionMessageToolStateCompleted),
            ["error"] = typeof(SessionMessageToolStateError),
            ["running"] = typeof(SessionMessageToolStateRunning),
            ["streaming"] = typeof(SessionMessageToolStateStreaming),
        }
        : new Dictionary<string, Type?>(StringComparer.Ordinal)
        {
            ["file"] = typeof(ToolFileContent),
            ["text"] = typeof(ToolTextContent),
        };
    private static readonly UnionTagTable<Type?> Table = Map.ToUnionTagTable();

    protected override TUnion Unknown(string marker, JsonElement payload) =>
        (TUnion)(State ? (object)new UnknownSessionMessageToolState(marker, payload) : new UnknownToolContent(marker, payload));

    public static OpenCodeJsonContext Create()
    {
        var options = new JsonSerializerOptions(OpenCodeJsonContext.Default.Options) { DefaultBufferSize = 16 };
        options.Converters.Insert(0, new ToolUnionMetadataOracle<ISessionMessageToolState>());
        options.Converters.Insert(0, new ToolUnionMetadataOracle<IToolContent>());
        return new OpenCodeJsonContext(options);
    }
}
