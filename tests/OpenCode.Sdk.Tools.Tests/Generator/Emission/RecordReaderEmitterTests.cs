using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

[ParallelLimiter<RoslynCompilationSlots>]
public sealed class RecordReaderEmitterTests
{
    [Test]
    public async Task Emit_Should_Render_A_Reader_And_Wire_Only_The_Selected_Union()
    {
        var plan = RecordReaderPlanFixture.Create();
        var sources = RecordReaderPlanFixture.Sources(plan);
        await Assert.That(sources.Select(static source => source.RelativePath)
            .Where(static path => path.EndsWith("JsonReader.cs", StringComparison.Ordinal)))
            .IsEquivalentTo(["Example/Internal/DenseRecordJsonReader.cs", "Example/Internal/SiblingRecordJsonReader.cs"], CollectionOrdering.Matching);
        var selected = EmitterSnapshot.Content(sources, "Example/Internal/ToolContentJsonConverter.cs");
        var unselected = EmitterSnapshot.Content(sources, "Example/Internal/OtherJsonConverter.cs");
        await Assert.That(selected).Contains("Of<DenseRecord>(DenseRecordJsonReader.Read)");
        await Assert.That(unselected).Contains("Of<DenseRecord>()");
        await Assert.That(unselected).DoesNotContain("DenseRecordJsonReader.Read");
        await Assert.That(EmitterSnapshot.Content(sources, "Example/Internal/DenseRecordJsonReader.cs"))
            .DoesNotContain("ExampleItemJsonReader");
        var normal = SourceEmitter.Emit(plan);
        await Assert.That(normal.Select(static source => source.RelativePath)).DoesNotContain("Example/Internal/DenseRecordJsonReader.cs");
        await Verify(EmitterSnapshot.Content(sources, "Example/Internal/SiblingRecordJsonReader.cs"));
    }

    [Test]
    public async Task Read_Should_Match_Metadata_For_The_Synthetic_Corpus()
    {
        var plan = RecordReaderPlanFixture.Create();
        var oracle = await UnionReaderOracle.CreateAsync(plan, RecordReaderPlanFixture.Sources(plan));
        foreach (var row in await RecordReaderPlanFixture.RowsAsync(plan))
        {
            await oracle.CompareAsync(row);
        }
    }

    [Test]
    public async Task Read_Should_Keep_Focused_Metadata_Nested_Unions_Away_From_Product_Readers()
    {
        var plan = RecordReaderPlanFixture.Create();
        var sources = RecordReaderPlanFixture.Sources(plan).ToList();
        var name = ConverterNamePolicy.Reader("DenseRecord");
        var source = sources.Single(source => source.RelativePath.EndsWith("/" + name + ".cs", StringComparison.Ordinal));
        var syntax = await CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(source.Utf8Source.Span)).GetRootAsync();
        var declaration = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var method = declaration.Members.OfType<MethodDeclarationSyntax>().Single();
        sources[sources.IndexOf(source)] = MetadataUnionOracleEmitter.Source(source.RelativePath,
            syntax.ReplaceNode(declaration, declaration.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                method.WithBody(SyntaxFactory.Block(SyntaxFactory.ParseStatement("throw new JsonException(\"product-reader-entered\");")))))).ToFullString());
        sources.AddRange(plan.Unions.Select(union => MetadataUnionOracleEmitter.Converter(union, plan, "Metadata" + ConverterNamePolicy.Of(union.ConceptName))));
        sources.Add(MetadataUnionOracleEmitter.Source("Internal/Serialization/MetadataUnionJsonConverter.cs",
            new FixtureLoader().Load("Serialization.MetadataUnionJsonConverter.cs")));
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var product = UnionReaderOracle.Context(assembly);
        var options = new JsonSerializerOptions(product.Options);
        foreach (var union in plan.Unions)
        {
            options.Converters.Insert(0, (JsonConverter)Activator.CreateInstance(assembly.GetType(plan.Namespaces.Internal + ".Metadata" + ConverterNamePolicy.Of(union.ConceptName), true)!)!);
        }
        var focused = (JsonSerializerContext)Activator.CreateInstance(product.GetType(), options)!;
        var seed = (await RecordCorpusGenerator.GenerateAsync(plan, ["DenseRecord"]))[1];
        var actual = UnionReaderOracle.Observe(product, "ISessionMessageToolState", seed.Json);
        await Assert.That(actual.Phase).IsEqualTo("read");
        await Assert.That(actual.Message).Contains("product-reader-entered");
        var expected = UnionReaderOracle.Observe(focused, "ISessionMessageToolState", seed.Json);
        await Assert.That(expected.Phase).IsEqualTo("success");
        await Assert.That(expected.Type).IsEqualTo("OpenCode.Sdk.Models.DenseRecord");
    }

    [Test]
    public async Task Read_Should_Keep_Strict_Numbers_Strict()
    {
        var plan = RecordReaderPlanFixture.Create();
        var oracle = await UnionReaderOracle.CreateAsync(plan, RecordReaderPlanFixture.Sources(plan));
        var seed = (await RecordCorpusGenerator.GenerateAsync(plan, ["DenseRecord"]))[1];
        var numericString = await RecordCorpusGenerator.ElementAsync(static writer => writer.WriteStringValue("17"));
        var row = seed with
        {
            Name = "dense/strict-numeric-string",
            IsSeed = false,
            Json = await RecordCorpusGenerator.VaryAsync(seed.Json, "strict", "replace", numericString)
        };
        await oracle.CompareAsync(row);
        await Assert.That(UnionReaderOracle.Observe(oracle.Product, "IToolContent", row.Json).Phase).IsEqualTo("read");
    }

    [Test]
    public async Task Read_Should_Refuse_A_Nonbinary_Null_Before_A_Valid_Duplicate()
    {
        var plan = RecordReaderPlanFixture.Create();
        var oracle = await UnionReaderOracle.CreateAsync(plan, RecordReaderPlanFixture.Sources(plan));
        var row = (await RecordCorpusGenerator.GenerateAsync(plan, ["DenseRecord"]))
            .Single(static row => row.Name == "tool-content/dense/null-then-valid/text");
        await Assert.That(UnionReaderOracle.Observe(oracle.Metadata, "IToolContent", row.Json).Phase).IsEqualTo("read");
        await oracle.CompareAsync(row);
    }
}
