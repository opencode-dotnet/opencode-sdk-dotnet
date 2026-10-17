using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// Generation writes each source to a path named after the type in it, and the repository's
/// post-generation format pass then canonicalizes that source. A name the format pass rewrites
/// therefore moves the declaration without moving the file, which surfaces as MA0048 on a tree
/// nobody may hand-edit. The nullable-wrapped inline union below is the shape that produced it:
/// its promoted branch key ends in an ordinal, and a guarded ordinal spells an interior
/// underscore.
/// </summary>
public sealed class GeneratedFileNameTests
{
    private const string GroupName = "preference";
    private const string OperationId = "preference.get";

    [Test]
    public async Task Bind_Should_Name_An_Ordinal_Union_Branch_Without_An_Interior_Underscore()
    {
        var plan = await BindNullableInlineUnionPlanAsync();

        var names = plan.Models.Select(static model => model.Name).ToArray();

        await Assert.That(names).Contains("PreferencePatchSearch0");
        await Assert.That(names.Where(static name => name.Contains('_', StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task Emit_Should_Name_Every_Source_After_The_Type_It_Declares()
    {
        var sources = SourceEmitter.Emit(await BindNullableInlineUnionPlanAsync());

        var mismatches = sources
            .Select(static source => (source.RelativePath, Declared: FirstDeclaredTypeName(source)))
            .Where(static entry => entry.Declared is not null
                                   && !string.Equals(FileStem(entry.RelativePath), entry.Declared, StringComparison.Ordinal))
            .Select(static entry => $"{entry.RelativePath} declares {entry.Declared}")
            .ToArray();

        await Assert.That(mismatches).IsEmpty();

        // The emitter writes file and declaration from one string, so the pair can only come
        // apart afterwards - the format pass rewrites an interior underscore out of the
        // declaration and leaves the file name behind. Refusing the spelling is what holds.
        var rewritable = sources
            .Select(FirstDeclaredTypeName)
            .Where(static declared => declared is not null && declared.Contains('_', StringComparison.Ordinal))
            .ToArray();

        await Assert.That(rewritable).IsEmpty();
    }

    /// <summary>
    /// A tri-state converter is named from the CLR instantiation, not from anything the document
    /// spells, so the file-equals-type rule has to be proven for that naming path too.
    /// </summary>
    [Test]
    public async Task Emit_Should_Name_Every_Optional_Converter_After_The_Type_It_Declares()
    {
        var sources = SourceEmitter.Emit(EmitterPlanFixture.Create());

        var converters = sources
            .Where(static source => source.RelativePath.Contains("OptionalOf", StringComparison.Ordinal))
            .ToArray();

        // This checks the complete inventory, not its enumeration order.
        await Assert
            .That(converters.Select(static source => source.RelativePath))
            .IsEquivalentTo(
            [
                "Internal/Serialization/OptionalOfBooleanJsonConverter.cs",
                "Internal/Serialization/OptionalOfStringJsonConverter.cs",
            ], CollectionOrdering.Any);
        var mismatches = converters
            .Select(static source => (source.RelativePath, Declared: FirstDeclaredTypeName(source)))
            .Where(static entry => !string.Equals(FileStem(entry.RelativePath), entry.Declared, StringComparison.Ordinal))
            .Select(static entry => $"{entry.RelativePath} declares {entry.Declared}")
            .ToArray();

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>MA0048's rule: the file name up to its first dot names the type, so a partial part may be <c>Type.Part.cs</c>.</summary>
    private static string FileStem(string relativePath) => relativePath.Split('/')[^1].Split('.')[0];

    private static string? FirstDeclaredTypeName(GeneratedSource source) => SyntaxFactory
        .ParseCompilationUnit(Encoding.UTF8.GetString(source.Utf8Source.ToArray()))
        .DescendantNodes()
        .OfType<BaseTypeDeclarationSyntax>()
        .Select(static declaration => declaration.Identifier.ValueText)
        .FirstOrDefault();

    /// <summary>
    /// Mirrors the pinned document's <c>Config.PreferencesPatch.websearch</c>: a nullable
    /// <c>anyOf</c> wrapping a structural choice, so the inner union is promoted under the
    /// wrapper's branch ordinal rather than under a marker.
    /// </summary>
    private static async Task<EmitPlan> BindNullableInlineUnionPlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => _ = spec
            .WithSchema("Preference.Detail", schema => schema
                .Type("object")
                .Property("label", property => property.Type("string"), required: true))
            .WithSchema("Preference.Patch", schema => schema
                .Type("object")
                .Property("search", property => property.AnyOf(
                    branch => branch.AnyOf(
                        value => value.Type("boolean"),
                        value => value.Ref("Preference.Detail")),
                    branch => branch.Type("null"))))
            .WithSchema("Preference.Container", schema => schema
                .Type("object")
                .Property("value", property => property.Ref("Preference.Patch"), required: true))
            .WithOperation(OperationId, configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("Preference.Container")))));

        return new BindingTestHost().Bind(document, Selection(OperationId), Curation(Groups(GroupName, RootGroup())));
    }
}
