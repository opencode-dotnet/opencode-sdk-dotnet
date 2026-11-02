using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal sealed class RecordCorpusGenerator
{
    private static readonly string[] BooleanTags = ["true", "false"];
    private readonly EmitPlan _plan;
    private readonly Dictionary<string, ModelPlan> _models;
    private readonly Dictionary<string, UnionPlan> _unions;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _sites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _choices = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool _maximal;

    private RecordCorpusGenerator(EmitPlan plan)
    {
        _plan = plan;
        _models = plan.Models.ToDictionary(static model => model.Name, StringComparer.Ordinal);
        _unions = plan.Unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
    }

    public static Task<IReadOnlyList<RecordCorpusRow>> GenerateAsync(EmitPlan plan, IReadOnlyList<string> recordNames)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recordNames);
        return new RecordCorpusGenerator(plan).RowsAsync(recordNames);
    }

    private async Task<IReadOnlyList<RecordCorpusRow>> RowsAsync(IReadOnlyList<string> names)
    {
        var rows = new List<RecordCorpusRow>();
        foreach (var name in names)
        {
            var model = (ObjectModelPlan)_models[name];
            var route = _plan.Unions.FirstOrDefault(union => union.Variants.Any(arm => arm.TypeName == name) || union.PrefixVariant?.TypeName == name);
            var label = route is null ? name : Label(route, name);
            _sites.Clear();
            _choices = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var maximal in new[] { false, true })
            {
                _maximal = maximal;
                var seed = await BuildAsync(writer => WriteRecord(writer, model, name, []));
                rows.Add(new($"{label}/{(maximal ? "maximal" : "minimal")}", name, route?.Name, seed, true));
            }

            // Discover descendants as their ancestor arms are visited, without combining siblings.
            for (var index = 0; index < _sites.Count; index++)
            {
                var site = _sites.ElementAt(index);
                _choices = site.Value;
                foreach (var maximal in new[] { false, true })
                {
                    _maximal = maximal;
                    rows.Add(new($"{label}/branch/{site.Key}/{(maximal ? "maximal" : "minimal")}", name, route?.Name,
                        await BuildAsync(writer => WriteRecord(writer, model, name, [])), true));
                }
            }

            _choices = new Dictionary<string, string>(StringComparer.Ordinal);
            var maximalRow = rows.Single(row => row.Name == label + "/maximal");
            await AddVariationsAsync(rows, maximalRow, model, label);
            if (route is not null)
            {
                var unknown = route.MarkerKind == LiteralKind.Boolean
                    ? BooleanTags.Except(route.Variants.Select(static arm => arm.Tag).Concat(route.KnownImpossibleTags), StringComparer.Ordinal) : ["oracle.future"];
                foreach (var tag in route.KnownImpossibleTags.Concat(unknown))
                {
                    rows.Add(new($"{label}/tag/{tag}", name, route.Name,
                        await VaryAsync(maximalRow.Json, route.MarkerWireName, "replace", await ElementAsync(writer => WriteMarker(writer, route.MarkerKind, tag)))));
                }
            }
        }

        return rows;
    }

    private static string Label(UnionPlan union, string name)
    {
        var tag = union.PrefixVariant is { } prefix && prefix.TypeName == name
            ? prefix.Prefix + "oracle" : union.Variants.Single(arm => arm.TypeName == name).Tag;
        return union.Name switch
        {
            "IToolContent" => "tool-content/" + tag,
            "ISessionMessageToolState" => "state/" + tag,
            _ => union.ConceptName + "/" + tag,
        };
    }

    private static async Task AddVariationsAsync(List<RecordCorpusRow> rows, RecordCorpusRow seed, ObjectModelPlan model, string label)
    {
        using var document = JsonDocument.Parse(seed.Json);
        foreach (var member in model.Properties.Where(property =>
                     !model.ChainMarkers.Any(marker => marker.WireName == property.WireName && marker.Prefix is null)))
        {
            var key = member.WireName;
            if (!document.RootElement.TryGetProperty(key, out var value))
            {
                continue; // A recursive optional edge's finite exit is absence.
            }

            if (member.IsRequired)
            {
                rows.Add(seed with { Name = $"{label}/missing/{key}", Json = await VaryAsync(seed.Json, key, "omit", default), IsSeed = false });
            }

            foreach (var change in new[] { "null", "wrong-token", "duplicate", "null-then-valid" })
            {
                var replacement = await ElementAsync(writer =>
                {
                    if (change is "null" or "null-then-valid")
                    {
                        writer.WriteNullValue();
                    }
                    else if (change == "wrong-token")
                    {
                        if (value.ValueKind == JsonValueKind.String)
                        {
                            writer.WriteNumberValue(17);
                        }
                        else
                        {
                            writer.WriteStringValue("wrong-token");
                        }
                    }
                    else if (value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue("first." + key);
                    }
                    else
                    {
                        value.WriteTo(writer);
                    }
                });
                rows.Add(seed with { Name = $"{label}/{change}/{key}", Json = await VaryAsync(seed.Json, key, change, replacement), IsSeed = false });
            }
        }

        foreach (var order in new[] { "reordered", "escaped" })
        {
            rows.Add(seed with { Name = $"{label}/{order}", Json = await VaryAsync(seed.Json, "", order, default), IsSeed = false });
        }
    }

    private void WriteRecord(Utf8JsonWriter writer, ObjectModelPlan model, string path, IReadOnlyList<string> visiting)
    {
        if (visiting.Contains(model.Name, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Required record cycle has no finite exit at '{path}'.");
        }

        var stack = visiting.Append(model.Name).ToArray();
        writer.WriteStartObject();
        foreach (var property in model.Properties)
        {
            if (!_maximal && !property.IsRequired && !Enters(path + "." + property.WireName))
            {
                continue;
            }

            var recursive = property.Type is NamedTypeReferencePlan && Reenters(property.Type, stack);
            if (recursive && !property.IsRequired)
            {
                continue;
            }

            writer.WritePropertyName(property.WireName);
            if (model.ChainMarkers.FirstOrDefault(marker => marker.WireName == property.WireName)?.Prefix is { } prefix)
            {
                writer.WriteStringValue(prefix + "oracle");
            }
            else if (property.IsLiteral)
            {
                WriteMarker(writer, property.LiteralKind!.Value, property.LiteralValue!);
            }
            else if (recursive && property.Type.IsNullable)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteValue(writer, property.Type, path + "." + property.WireName, stack);
            }
        }

        writer.WriteEndObject();
    }

    private void WriteValue(Utf8JsonWriter writer, TypeReferencePlan type, string path, IReadOnlyList<string> stack)
    {
        switch (type)
        {
            case ListTypeReferencePlan list:
                writer.WriteStartArray();
                if ((_maximal || Enters(path + "[]")) && !Reenters(list.ElementType, stack))
                {
                    if (list.ElementType is NamedTypeReferencePlan element && _unions.TryGetValue(element.Name, out var union))
                    {
                        var arms = Arms(union);
                        _ = Choose(path + "[]", arms);
                        foreach (var arm in _choices.TryGetValue(path + "[]", out var selected) ? [selected] : arms)
                        {
                            var previous = _choices;
                            _choices = new Dictionary<string, string>(_choices, StringComparer.Ordinal) { [path + "[]"] = arm };
                            WriteNamed(writer, arm, $"{path}[]/{arm}", stack);
                            _choices = previous;
                        }
                    }
                    else
                    {
                        WriteValue(writer, list.ElementType, path + "[]", stack);
                    }
                }
                writer.WriteEndArray();
                break;
            case DictionaryTypeReferencePlan dictionary:
                writer.WriteStartObject();
                if ((_maximal || Enters(path + ".entry")) && !Reenters(dictionary.ValueType, stack))
                {
                    writer.WritePropertyName("entry");
                    WriteValue(writer, dictionary.ValueType, path + ".entry", stack);
                }
                writer.WriteEndObject();
                break;
            case NamedTypeReferencePlan named: WriteNamed(writer, named.Name, path, stack); break;
            case BinaryTypeReferencePlan: writer.WriteBase64StringValue("corpus"u8); break;
            case SpecialNumberTypeReferencePlan: writer.WriteNumberValue(Number(path) + 0.5); break;
            default: throw new InvalidOperationException($"Unsupported corpus type at '{path}': {type}.");
        }
    }

    private bool Reenters(TypeReferencePlan type, IReadOnlyList<string> stack) => type switch
    {
        NamedTypeReferencePlan named => stack.Contains(named.Name, StringComparer.Ordinal)
            || (_models.GetValueOrDefault(named.Name) is ObjectModelPlan record && record.Properties
                .Any(property => property.IsRequired && !property.Type.IsNullable && !property.Type.IsCollection
                    && Reenters(property.Type, [.. stack, named.Name]))),
        ListTypeReferencePlan list => Reenters(list.ElementType, stack),
        DictionaryTypeReferencePlan dictionary => Reenters(dictionary.ValueType, stack),
        _ => false,
    };

    private void WriteNamed(Utf8JsonWriter writer, string name, string path, IReadOnlyList<string> stack)
    {
        if (_unions.TryGetValue(name, out var union))
        {
            var selected = Choose(path, Arms(union));
            WriteNamed(writer, selected, $"{path}/{selected}", stack);
            return;
        }

        if (_models.TryGetValue(name, out var model))
        {
            switch (model)
            {
                case ObjectModelPlan record: WriteRecord(writer, record, path, stack); break;
                case EnumModelPlan enumeration: writer.WriteStringValue(enumeration.Values[0].WireValue); break;
                case StructuralUnionModelPlan structural:
                    var choice = Choose(path, [.. structural.Arms.Select(static arm => arm.Name)]);
                    var arm = structural.Arms.Single(arm => arm.Name == choice);
                    WriteValue(writer, arm.Type, $"{path}/{arm.Name}", stack);
                    break;
            }
            return;
        }

        switch (name)
        {
            case "string": case "Uri": writer.WriteStringValue(path); break;
            case "bool": writer.WriteBooleanValue(true); break;
            case "long":
            case "int":
            case "double":
                writer.WriteNumberValue(Number(path));
                break;
            case "JsonElement":
                writer.WriteStartObject();
                writer.WriteStartArray("values");
                writer.WriteStringValue(path);
                writer.WriteNullValue();
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            default: throw new InvalidOperationException($"No corpus value for '{name}' at '{path}'.");
        }
    }

    private int Number(string path)
    {
        _ = _numbers.TryAdd(path, _numbers.Count + 1);
        return _numbers[path];
    }

    private static IReadOnlyList<string> Arms(UnionPlan union) => [.. union.Variants.Select(static arm => arm.TypeName)
        .Concat(union.PrefixVariant is { } prefix ? [prefix.TypeName] : [])];

    private bool Enters(string path) => _choices.Keys.Any(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)
        || key.StartsWith(path + ".", StringComparison.Ordinal) || key.StartsWith(path + "[]", StringComparison.Ordinal));

    private string Choose(string path, IReadOnlyList<string> arms)
    {
        foreach (var arm in arms)
        {
            var ancestors = _choices.Where(pair => path.StartsWith(pair.Key + "/", StringComparison.Ordinal))
                .ToDictionary(StringComparer.Ordinal);
            ancestors[path] = arm;
            _ = _sites.TryAdd(path + "/" + arm, ancestors);
        }
        return _choices.GetValueOrDefault(path, arms[0]);
    }

    private static void WriteMarker(Utf8JsonWriter writer, LiteralKind kind, string value)
    {
        if (kind == LiteralKind.String)
        {
            writer.WriteStringValue(value);
        }
        else if (kind == LiteralKind.Boolean)
        {
            writer.WriteBooleanValue(bool.Parse(value));
        }
        else
        {
            writer.WriteNumberValue(double.Parse(value, CultureInfo.InvariantCulture));
        }
    }

    internal static async Task<byte[]> VaryAsync(byte[] json, string key, string change, JsonElement replacement)
    {
        using var document = JsonDocument.Parse(json);
        return await BuildAsync(writer =>
        {
            writer.WriteStartObject();
            var members = document.RootElement.EnumerateObject().ToArray();
            foreach (var member in change == "reordered" ? members.Reverse() : members)
            {
                if (member.Name == key && change == "omit")
                {
                    continue;
                }

                if (member.Name == key && change is "duplicate" or "null-then-valid")
                {
                    writer.WritePropertyName(key);
                    replacement.WriteTo(writer);
                }
                if (change == "escaped")
                {
                    writer.WritePropertyName(JsonEncodedText.Encode(member.Name, JavaScriptEncoder.Create(UnicodeRanges.None)));
                }
                else
                {
                    writer.WritePropertyName(member.Name);
                }

                if (member.Name == key && change is "null" or "wrong-token" or "replace")
                {
                    replacement.WriteTo(writer);
                }
                else
                {
                    member.Value.WriteTo(writer);
                }
            }
            if (change == "append")
            {
                writer.WritePropertyName(key);
                replacement.WriteTo(writer);
            }
            writer.WriteEndObject();
        });
    }

    internal static async Task<JsonElement> ElementAsync(Action<Utf8JsonWriter> write)
    {
        using var document = JsonDocument.Parse(await BuildAsync(write));
        return document.RootElement.Clone();
    }

    private static async Task<byte[]> BuildAsync(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        await writer.FlushAsync();
        return buffer.WrittenSpan.ToArray();
    }
}
