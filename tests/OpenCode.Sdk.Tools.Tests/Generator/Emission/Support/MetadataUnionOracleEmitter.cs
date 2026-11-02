using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal static class MetadataUnionOracleEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var replacements = plan.Unions.ToDictionary(union => GeneratedPlacement.ConverterPathOf(plan.Placement,
            union.Name, ConverterNamePolicy.Of(union.ConceptName)), union => Converter(union, plan), StringComparer.Ordinal);
        return [.. SourceEmitter.Emit(plan).Where(static source => !source.RelativePath.EndsWith("JsonReader.cs", StringComparison.Ordinal))
            .Select(source => replacements.GetValueOrDefault(source.RelativePath, source)),
            Source("Internal/Serialization/MetadataUnionJsonConverter.cs", new FixtureLoader().Load("Serialization.MetadataUnionJsonConverter.cs"))];
    }

    private static GeneratedSource Converter(UnionPlan union, EmitPlan plan)
    {
        var name = ConverterNamePolicy.Of(union.ConceptName);
        var entries = union.Variants.Where(variant => variant.MarkerWireName == union.MarkerWireName)
            .Select(static variant => $"[{Quote(variant.Tag)}] = typeof({variant.TypeName}),")
            .Concat(union.KnownImpossibleTags.Select(static tag => $"[{Quote(tag)}] = null,"));
        var prefix = union.PrefixVariant is { } arm
            ? $"protected override string Prefix => {Quote(arm.Prefix)}; protected override Type PrefixType => typeof({arm.TypeName});" : "";
        var fixedCheck = "";
        if (union.FixedMarker is { } marker)
        {
            var value = marker.Kind == LiteralKind.String ? Quote(marker.Value) : marker.Value.ToLowerInvariant();
            fixedCheck = $"protected override void RequireFixed(ref Utf8JsonReader reader) => new UnionDiscriminatorReader().Require{marker.Kind}(ref reader, {Quote(marker.WireName)}, {value}, {Quote(union.ConceptName)});";
        }
        var text = $$"""
            using System;
            using System.Collections.Generic;
            using System.Text.Json;
            using {{plan.Namespaces.Models}};
            namespace {{plan.Namespaces.Internal}};
            internal sealed class {{name}} : MetadataUnionJsonConverter<{{union.Name}}>
            {
                protected override string Concept => {{Quote(union.ConceptName)}};
                protected override string Marker => {{Quote(union.MarkerWireName)}};
                protected override bool BooleanMarker => {{(union.MarkerKind == LiteralKind.Boolean ? "true" : "false")}};
                private static readonly UnionTagTable<Type?> Map = new Dictionary<string, Type?>(StringComparer.Ordinal)
                { {{string.Join('\n', entries)}} }.ToUnionTagTable();
                protected override UnionTagTable<Type?> Types => Map;
                {{prefix}}
                {{fixedCheck}}
                protected override {{union.Name}} Unknown(string marker, JsonElement payload) => new {{union.UnknownTypeName}}({{(union.MarkerKind == LiteralKind.Boolean ? "bool.Parse(marker)" : "marker")}}, payload);
            }
            """;
        return Source(GeneratedPlacement.ConverterPathOf(plan.Placement, union.Name, name), text);
    }

    internal static GeneratedSource Source(string path, string text) => new() { RelativePath = path, Utf8Source = Encoding.UTF8.GetBytes(text) };
    private static string Quote(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
