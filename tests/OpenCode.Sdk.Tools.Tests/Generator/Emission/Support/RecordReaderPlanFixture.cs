using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal static class RecordReaderPlanFixture
{
    public static IReadOnlySet<(string Union, string Variant)> Rollout { get; } = new HashSet<(string, string)>
    {
        ("IToolContent", "DenseRecord"), ("IToolContent", "SiblingRecord"), ("IToolContent", "NestedRecord"),
    };

    public static EmitPlan Create()
    {
        var plan = EmitterPlanFixture.Create();
        var nested = Union("IToolContent", Arm("DenseRecord") with { Tag = "dense", MarkerWireName = "kind" },
            Arm("SiblingRecord") with { Tag = "sibling", MarkerWireName = "kind" }) with
        {
            MarkerWireName = "kind",
            MarkerName = "Kind",
            BaseTypeName = "ISessionMessageToolState",
            FixedMarker = new() { WireName = "type", Name = "Type", Kind = Tools.Generator.Ingestion.Models.LiteralKind.String, Value = "tool" },
        };
        var outer = Union("ISessionMessageToolState", NestedArm("IToolContent") with { Tag = "tool" });
        var other = nested with { Name = "IOther", ConceptName = "Other", UnknownTypeName = "UnknownOther", BaseTypeName = null };
        ModelPropertyPlan[] properties =
        [
            StringLiteral("type", "tool"), StringLiteral("kind", "dense"),
            Property("text", Named("string"), true), Property("note", Named("string", true), false),
            Property("flag", Named("bool"), true), Property("strict", Named("double"), true),
            Property("nullableNumber", Named("long", true), true), Property("special", SpecialNumber(), true),
            Property("nullableSpecial", SpecialNumber(true), false), Property("specialList", List(SpecialNumber()), true),
            Property("specialMap", Map(SpecialNumber()), true),
            Property("items", List(Named("string")), true), Property("nullableItems", List(Named("string"), true), true),
            Property("map", Map(Named("JsonElement") with { JsonNullRepresentation = JsonNullRepresentation.InBand }), true),
            Property("nullableMap", Map(Named("JsonElement") with { JsonNullRepresentation = JsonNullRepresentation.InBand }, true), false),
            Property("child", Named("ExampleItem"), true), Property("nullableChild", Named("ExampleItem", true), false),
            Property("any", Named("JsonElement") with { JsonNullRepresentation = JsonNullRepresentation.InBand }, true),
            Property("optionalAny", Named("JsonElement", true), false),
            Property("patch", Named("string", true), false, true), Property("patchNumber", SpecialNumber(true), false, true),
            Property("patchMap", Map(Named("JsonElement") with { JsonNullRepresentation = JsonNullRepresentation.InBand }, true), false, true),
            Property("binary", new BinaryTypeReferencePlan { IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull }, true),
            Property("mode", Named("ExampleMode"), true), StringLiteral("literal", "ordinary"),
            .. Enumerable.Range(0, 65).Select(static index => Property("n" + index.ToString(CultureInfo.InvariantCulture), Named("long"), true)),
        ];
        var dense = Record("DenseRecord", properties) with { ImplementedUnionNames = ["IToolContent", "IOther"] };
        var sibling = Record("SiblingRecord", StringLiteral("type", "tool"), StringLiteral("kind", "sibling"),
            Property("text", Named("string"), true), Property("note", Named("string", true), false)) with
        { ImplementedUnionNames = ["IToolContent", "IOther"] };
        return UnionChainBuilder.Resolve((plan with
        {
            Models = [.. plan.Models, dense, sibling],
            Unions = [.. plan.Unions, outer, nested, other],
            Registry = plan.Registry with
            {
                TypeNames = [.. plan.Registry.TypeNames, "DenseRecord", "SiblingRecord", "IToolContent",
                "UnknownToolContent", "ISessionMessageToolState", "UnknownSessionMessageToolState", "IOther", "UnknownOther"]
            },
        }).WithSingleSlice());
    }

    public static EmitPlan CreateNested()
    {
        var plan = Create();
        var record = Record("NestedRecord", StringLiteral("type", "tool"), StringLiteral("kind", "nested"),
            Property("child", Named("NestedValue"), true), Property("nullableChild", Named("NestedValue", true), false),
            Property("metadataChild", Named("NestedValue"), true)) with
        { ImplementedUnionNames = ["IToolContent", "IOther"] };
        return SelectReaders(UnionChainBuilder.Resolve((plan with
        {
            Models = [.. plan.Models, record, Record("NestedValue", Property("value", Named("long"), true), Property("note", Named("string", true), false))],
            Unions = [.. plan.Unions.Select(union => union.Name is "IToolContent" or "IOther"
                ? union with { Variants = [.. union.Variants, Arm("NestedRecord") with { Tag = "nested", MarkerWireName = "kind" }] } : union)],
            Registry = plan.Registry with { TypeNames = [.. plan.Registry.TypeNames, "NestedRecord", "NestedValue"] },
            ReaderCallSites = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["NestedRecord"] = new HashSet<string>(StringComparer.Ordinal) { "Child", "NullableChild" },
            },
        }).WithSingleSlice()));
    }

    public static EmitPlan SelectReaders(EmitPlan plan)
    {
        var readers = RecordReaderPolicy.Select(plan.Models, plan.Unions, out var readerArms, Rollout, plan.ReaderCallSites);
        return plan with { ReaderRecords = readers, ReaderArms = readerArms };
    }

    public static IReadOnlyList<GeneratedSource> Sources(EmitPlan plan) => SourceEmitter.Emit(SelectReaders(plan));

    public static async Task<IReadOnlyList<RecordCorpusRow>> NestedRowsAsync(EmitPlan plan)
    {
        var seeds = (await RecordCorpusGenerator.GenerateAsync(plan, ["NestedRecord"]))
            .Where(static row => row.IsSeed && row.UnionType == "IToolContent").ToList();
        var maximal = seeds.Single(static row => row.Name.EndsWith("/maximal", StringComparison.Ordinal));
        foreach (var change in new[] { "omit", "null" })
        {
            seeds.Add(maximal with
            {
                Name = "nested/nullable-" + change,
                Json = await RecordCorpusGenerator.VaryAsync(
                maximal.Json, "nullableChild", change, await RecordCorpusGenerator.ElementAsync(static writer => writer.WriteNullValue()))
            });
        }
        using var document = JsonDocument.Parse(maximal.Json);
        var child = System.Text.Encoding.UTF8.GetBytes(document.RootElement.GetProperty("child").GetRawText());
        foreach (var change in new[] { "omit", "wrong-token", "null-then-valid" })
        {
            using var nested = JsonDocument.Parse(await RecordCorpusGenerator.VaryAsync(child, "value", change,
                await RecordCorpusGenerator.ElementAsync(writer =>
                {
                    if (change == "wrong-token")
                    {
                        writer.WriteStringValue("wrong-token");
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }
                })));
            seeds.Add(maximal with
            {
                Name = "nested/required-" + change,
                IsSeed = false,
                Json = await RecordCorpusGenerator.VaryAsync(maximal.Json, "child", "replace", nested.RootElement)
            });
        }
        return seeds;
    }

    public static async Task<IReadOnlyList<RecordCorpusRow>> RowsAsync(EmitPlan plan)
    {
        var rows = (await RecordCorpusGenerator.GenerateAsync(plan, ["DenseRecord", "SiblingRecord"])).ToList();
        var seed = rows.Single(static row => row.Name == "ToolContent/dense/maximal");
        foreach (var key in new[] { "binary", "note", "nullableNumber", "nullableSpecial", "nullableItems", "nullableMap", "nullableChild", "patch", "patchMap" })
        {
            rows.Add(seed with
            {
                Name = "dense/valid-then-null/" + key,
                IsSeed = false,
                Json = await RecordCorpusGenerator.VaryAsync(seed.Json, key, "append", await RecordCorpusGenerator.ElementAsync(static writer => writer.WriteNullValue()))
            });
        }
        rows.AddRange(await NamedNumberRowsAsync(seed));
        foreach (var key in new[] { "flag", "strict", "n0", "n64", "mode", "literal" })
        {
            var value = await RecordCorpusGenerator.ElementAsync(writer =>
            {
                if (key == "flag")
                {
                    writer.WriteBooleanValue(false);
                }
                else if (key is "strict" or "n0" or "n64")
                {
                    writer.WriteNumberValue(987);
                }
                else
                {
                    writer.WriteStringValue("different");
                }
            });
            rows.Add(seed with
            {
                Name = "dense/distinct-duplicate/" + key,
                IsSeed = false,
                Json = await RecordCorpusGenerator.VaryAsync(seed.Json, key, "duplicate", value)
            });
        }
        foreach (var key in new[] { "type", "kind" })
        {
            var value = await RecordCorpusGenerator.ElementAsync(static writer => writer.WriteNullValue());
            foreach (var change in new[] { "omit", "null", "duplicate", "append" })
            {
                rows.Add(seed with
                {
                    Name = "dense/ignored-marker/" + key + "/" + change,
                    IsSeed = false,
                    Json = await RecordCorpusGenerator.VaryAsync(seed.Json, key, change, value)
                });
            }
        }
        var unknown = await RecordCorpusGenerator.ElementAsync(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("nested");
            writer.WriteStartObject();
            writer.WriteBoolean("ignored", true);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        var withUnknown = await RecordCorpusGenerator.VaryAsync(seed.Json, "unknown", "append", unknown);
        rows.Add(seed with
        {
            Name = "dense/unknown-before-known",
            IsSeed = false,
            Json = await RecordCorpusGenerator.VaryAsync(withUnknown, "text", "append",
                await RecordCorpusGenerator.ElementAsync(static writer => writer.WriteStringValue("after-unknown")))
        });
        var missing = seed.Json;
        foreach (var key in new[] { "flag", "n0", "n64" })
        {
            missing = await RecordCorpusGenerator.VaryAsync(missing, key, "omit", default);
        }

        rows.Add(seed with { Name = "dense/multiple-missing-across-mask-words", Json = missing, IsSeed = false });
        return rows;
    }

    private static async Task<IReadOnlyList<RecordCorpusRow>> NamedNumberRowsAsync(RecordCorpusRow seed)
    {
        var rows = new List<RecordCorpusRow>();
        foreach (var literal in new[] { "NaN", "Infinity", "-Infinity", "17" })
        {
            foreach (var key in new[] { "special", "nullableSpecial", "specialList", "specialMap", "patchNumber" })
            {
                var value = await RecordCorpusGenerator.ElementAsync(writer =>
                {
                    if (key == "specialList")
                    {
                        writer.WriteStartArray();
                    }
                    if (key == "specialMap")
                    {
                        writer.WriteStartObject();
                        writer.WritePropertyName("entry");
                    }
                    writer.WriteStringValue(literal);
                    if (key == "specialList")
                    {
                        writer.WriteEndArray();
                    }
                    if (key == "specialMap")
                    {
                        writer.WriteEndObject();
                    }
                });
                rows.Add(seed with
                {
                    Name = "dense/named/" + key + "/" + literal,
                    IsSeed = false,
                    Json = await RecordCorpusGenerator.VaryAsync(seed.Json, key, "replace", value)
                });
            }
        }
        return rows;
    }

    private static ListTypeReferencePlan List(TypeReferencePlan element, bool nullable = false) =>
        new() { ElementType = element, IsNullable = nullable, JsonNullRepresentation = JsonNullRepresentation.ClrNull };

    private static DictionaryTypeReferencePlan Map(TypeReferencePlan value, bool nullable = false) =>
        new() { ValueType = value, IsNullable = nullable, JsonNullRepresentation = JsonNullRepresentation.ClrNull };
}
