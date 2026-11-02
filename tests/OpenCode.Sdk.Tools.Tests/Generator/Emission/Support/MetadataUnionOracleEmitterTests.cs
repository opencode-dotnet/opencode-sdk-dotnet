using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class MetadataUnionOracleEmitterTests(PinnedBinding pinned)
{
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Keep_Nested_Structural_And_Optional_Record_Paths_In_The_Oracle_Image()
    {
        var plan = EmitterPlanFixture.Create();
        var root = Record("Root", Property("nested", Named("IExampleEvent"), true),
            Property("structural", Named("Box"), true), Property("optional", Named("CreatedEvent", true), false, true));
        var box = new StructuralUnionModelPlan
        {
            Name = "Box",
            Namespace = "OpenCode.Sdk.Models",
            KindTypeName = "BoxKind",
            Arms = [new() { Name = "Record", Type = Named("CreatedEvent"), Tokens = [JsonTokenType.StartObject] }],
        };
        plan = (plan with
        {
            Models = [.. plan.Models, root, box],
            Registry = plan.Registry with { TypeNames = [.. plan.Registry.TypeNames, "Root", "Box"] }
        }).WithSingleSlice();
        var oracle = await UnionReaderOracle.CreateAsync(plan);
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, ["Root"]);
        foreach (var row in rows)
        {
            await oracle.CompareAsync(row);
        }
        var maximal = UnionReaderOracle.Observe(oracle.Metadata, "Root", rows.Single(static row => row.Name == "Root/maximal").Json);
        await Assert.That(maximal.Phase).IsEqualTo("success");
        using var members = JsonDocument.Parse(maximal.Bytes);
        await Assert.That(members.RootElement.GetProperty("nested").GetProperty("type").GetString()).IsEqualTo("created");
        await Assert.That(members.RootElement.GetProperty("structural").GetProperty("type").GetString()).IsEqualTo("created");
        await Assert.That(members.RootElement.GetProperty("optional").GetProperty("type").GetString()).IsEqualTo("created");
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Close_Every_Registered_Type_Over_Only_Metadata()
    {
        var plan = RecordReaderPlanFixture.Create();
        var product = RecordReaderPlanFixture.Sources(plan);
        var readerNames = RecordReaderPolicy.Select(plan.Models, plan.Unions, out _).Keys.Select(ConverterNamePolicy.Reader).ToHashSet(StringComparer.Ordinal);
        await Assert.That(product.Select(static source => source.RelativePath.Split('/')[^1]))
            .Contains(ConverterNamePolicy.Reader("DenseRecord") + ".cs");
        var sources = MetadataUnionOracleEmitter.Emit(plan, product);
        var text = string.Join('\n', sources.Select(static source => Encoding.UTF8.GetString(source.Utf8Source.Span)));
        var roots = await Task.WhenAll(sources.Select(static source => CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(source.Utf8Source.Span)).GetRootAsync()));
        await Assert.That(roots.SelectMany(static root => root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Select(static type => type.Identifier.ValueText).Where(readerNames.Contains)).IsEmpty();
        await Assert.That(roots.SelectMany(static root => root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            .Where(member => readerNames.Contains(member.Expression.ToString()))).IsEmpty();
        await Assert.That(text).DoesNotContain("UnionArm<");
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan);
        await Assert.That(oracle.Metadata.Options.TypeInfoResolverChain)
            .IsEquivalentTo<IList<IJsonTypeInfoResolver>, IJsonTypeInfoResolver>([oracle.Metadata], CollectionOrdering.Matching);
        await Assert.That(oracle.Metadata.GetTypeInfo(typeof(MetadataUnionOracleEmitterTests))).IsNull();
        _ = await Assert.That(() => oracle.Metadata.Options.GetTypeInfo(typeof(MetadataUnionOracleEmitterTests))).Throws<NotSupportedException>();
        foreach (var property in oracle.Metadata.GetType().GetProperties().Where(static property =>
                     typeof(JsonTypeInfo).IsAssignableFrom(property.PropertyType)))
        {
            await Assert.That(property.GetValue(oracle.Metadata)).IsNotNull().Because(property.Name);
        }
    }
}
