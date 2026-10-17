using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class TypePlanBinder(
    IReadOnlyDictionary<string, SchemaNode> graph,
    IReadOnlyDictionary<string, string> names,
    BindingErrorCollector errors)
{
    private readonly BindingErrorCollector _errors = errors ?? throw new ArgumentNullException(nameof(errors));
    private readonly IReadOnlyDictionary<string, SchemaNode> _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    private readonly IReadOnlyDictionary<string, string> _names = names ?? throw new ArgumentNullException(nameof(names));

    public TypeReferencePlan? Bind(string schemaKey, string propertyName, SchemaNode schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentNullException.ThrowIfNull(schema);

        return BindCore(schema, $"{schemaKey}.{propertyName}", []);
    }

    public TypeReferencePlan? BindStructuralArm(string schemaKey, string armName, SchemaNode schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(armName);
        ArgumentNullException.ThrowIfNull(schema);

        return BindCore(schema, $"{schemaKey}/anyOf/{armName}", []);
    }

    private TypeReferencePlan? BindCore(SchemaNode schema, string subject, HashSet<string> aliases) => schema switch
    {
        RefNode reference => BindReference(reference, subject, aliases),
        PrimitiveNode primitive => BindPrimitive(primitive),
        LiteralNode { Kind: LiteralKind.Boolean } => Named("bool"),
        LiteralNode { Kind: LiteralKind.String } => Named("string"),
        LiteralNode { Kind: LiteralKind.Number } => Named("double"),
        LiteralNode literal => Refuse(subject, $"literal kind '{literal.Kind}' is not supported by the emitter"),
        ArrayNode array => BindArray(array, subject, aliases),
        DictionaryNode dictionary => BindDictionary(dictionary, subject, aliases),
        FreeFormObjectNode => DictionaryOf(JsonElement()),
        UnrestrictedNode => JsonElement(),
        NeverNode => Refuse(subject, "never schemas cannot materialize a .NET value"),
        SpecialNumberNode => SpecialNumber(),
        NullableNode nullable => BindNullable(nullable, subject, aliases),
        JsonStringNode => Refuse(subject, "JSON-encoded strings are not supported by the emitter"),
        EncodedStringNode { ContentEncoding: "base64" } => Binary(),
        EncodedStringNode encoded => Refuse(
            subject,
            $"content encoding '{encoded.ContentEncoding}' is not supported by the emitter; only base64 materializes as bytes"),
        TupleNode => Refuse(subject, "tuple schemas are not supported by the emitter"),
        UnionNode { Classification: UnionClassification.Structural } union
            when UnstructuredUnionPolicy.Collapse(union, _graph) is { } collapsed => BindCore(collapsed, subject, aliases),
        ObjectNode or EnumNode or UnionNode => Refuse(subject, "inline nominal schema was not promoted into the graph"),
        _ => Refuse(subject, $"schema node '{schema.GetType().Name}' is not supported by the emitter"),
    };

    private TypeReferencePlan? BindReference(RefNode reference, string subject, HashSet<string> aliases)
    {
        if (_names.TryGetValue(reference.Target, out var name))
        {
            return Named(name);
        }

        if (!_graph.TryGetValue(reference.Target, out var target))
        {
            return Refuse(subject, $"schema reference '{reference.Target}' is missing");
        }

        if (!aliases.Add(reference.Target))
        {
            return Refuse(subject, $"non-nominal schema alias cycle reaches '{reference.Target}'");
        }

        var result = BindCore(target, subject, aliases);
        _ = aliases.Remove(reference.Target);
        return result;
    }

    private static NamedTypeReferencePlan BindPrimitive(PrimitiveNode primitive) => primitive.Kind switch
    {
        PrimitiveKind.String when string.Equals(primitive.Format, "uri", StringComparison.Ordinal) => Named("Uri"),
        PrimitiveKind.String => Named("string"),
        PrimitiveKind.Number => Named("double"),
        PrimitiveKind.Integer => Named("long"),
        PrimitiveKind.Boolean => Named("bool"),
        _ => throw new InvalidOperationException($"Unknown primitive kind '{primitive.Kind}'."),
    };

    private ListTypeReferencePlan? BindArray(ArrayNode array, string subject, HashSet<string> aliases)
    {
        var item = BindCore(array.Item, subject, aliases);
        return item is null ? null : ListOf(item);
    }

    private DictionaryTypeReferencePlan? BindDictionary(DictionaryNode dictionary, string subject, HashSet<string> aliases)
    {
        var value = BindCore(dictionary.Value, subject, aliases);
        return value is null ? null : DictionaryOf(value);
    }

    private TypeReferencePlan? BindNullable(NullableNode nullable, string subject, HashSet<string> aliases)
    {
        var inner = BindCore(nullable.Inner, subject, aliases);
        return inner is null || inner.JsonNullRepresentation == JsonNullRepresentation.InBand
            ? inner
            : inner with
            {
                IsNullable = true
            };
    }

    private TypeReferencePlan? Refuse(string subject, string problem)
    {
        _errors.Add(BindingErrorCategory.Schema, subject, problem);
        return null;
    }

    private static NamedTypeReferencePlan Named(string name) =>
        new()
        {
            Name = name,
            IsNullable = false,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    private static NamedTypeReferencePlan JsonElement() =>
        new()
        {
            Name = "JsonElement",
            IsNullable = false,
            JsonNullRepresentation = JsonNullRepresentation.InBand,
        };

    private static SpecialNumberTypeReferencePlan SpecialNumber() =>
        new()
        {
            IsNullable = false,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    private static BinaryTypeReferencePlan Binary() =>
        new()
        {
            IsNullable = false,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    private static ListTypeReferencePlan ListOf(TypeReferencePlan elementType) =>
        new()
        {
            ElementType = elementType,
            IsNullable = false,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    private static DictionaryTypeReferencePlan DictionaryOf(TypeReferencePlan valueType) =>
        new()
        {
            ValueType = valueType,
            IsNullable = false,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };
}
