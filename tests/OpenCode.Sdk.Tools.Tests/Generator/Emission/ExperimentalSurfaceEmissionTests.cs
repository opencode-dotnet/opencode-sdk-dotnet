using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// Where the experimental mark lands: on a class whose operations are all experimental instead of
/// its members, on each experimental member of a mixed class, on every way into a marked class,
/// on the per-operation records and the types only experimental operations reach, and never on an
/// internal artifact. A consumer compilation proves the emitted id is the one the compiler reports.
/// </summary>
public sealed class ExperimentalSurfaceEmissionTests
{
    [Test]
    public async Task Emit_Should_Mark_An_All_Experimental_Handle_With_An_Unmarked_Empty_Collection()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("probe.get", path: "/api/experimental/probe/{id}", configure: static operation => operation
                .Parameter("id", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")))));
        var plan = new BindingTestHost().Bind(document, Selection("probe.get"),
            Curation(Groups("probe", ClientGroup("Probes", "ProbeClient", "id", experimentalDiagnostic: "OPENCODE001"))));
        var sources = SourceEmitter.Emit(plan);
        var handle = Type<ClassDeclarationSyntax>(sources, "ProbeClient");

        await Assert.That(Diagnostic(handle)).IsEqualTo("OPENCODE001");
        await Assert.That(handle.Members.OfType<MethodDeclarationSyntax>().All(static method => Diagnostic(method) is null)).IsTrue();
        var collection = Type<ClassDeclarationSyntax>(sources, "ProbesClient");
        await Assert.That(Diagnostic(collection)).IsNull();
        // The handle's methods are reported only where its type is named, so the factory that hands it out carries the id.
        await Assert.That(Diagnostic(collection.Members.OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "GetProbeClient")))
            .IsEqualTo("OPENCODE001");
    }

    [Test]
    public async Task Emit_Should_Mark_A_Pagination_Companion_Unless_Its_Class_Is_Marked()
    {
        var operation = EmitterPlanFixture.CreateClientPlans().SelectMany(static client => client.Operations)
            .Single(static operation => operation.Pagination is not null) with
        { ExperimentalDiagnostic = "OPENCODE001" };

        var method = PaginationMethodEmitter.Emit(operation, EmissionMode.Public);
        var classMarked = PaginationMethodEmitter.Emit(operation, EmissionMode.Public, classMarked: true);

        await Assert.That(Diagnostic(method)).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(classMarked)).IsNull();
    }

    [Test]
    public async Task Emit_Should_Mark_An_All_Experimental_Client_Instead_Of_Its_Methods()
    {
        var sources = SourceEmitter.Emit(await PlanAsync());
        var client = Type<ClassDeclarationSyntax>(sources, "ProbesClient");

        await Assert.That(Diagnostic(client)).IsEqualTo("OPENCODE001");
        await Assert.That(client.Members.OfType<MethodDeclarationSyntax>().All(static method => Diagnostic(method) is null)).IsTrue();
        var root = Type<ClassDeclarationSyntax>(sources, "OpenCodeClient");
        await Assert.That(Diagnostic(root.Members.OfType<PropertyDeclarationSyntax>().Single(static property => property.Identifier.ValueText == "Probes")))
            .IsEqualTo("OPENCODE001");
    }

    [Test]
    public async Task Emit_Should_Mark_Only_The_Experimental_Method_On_A_Mixed_Client()
    {
        var plan = await PlanAsync(stable: true);
        var client = Type<ClassDeclarationSyntax>(SourceEmitter.Emit(plan), "ProbesClient");
        var experimental = plan.Clients.SelectMany(static value => value.Operations).Single(static operation => operation.OperationId == "probe.get");
        var stable = plan.Clients.SelectMany(static value => value.Operations).Single(static operation => operation.OperationId == "probe.list");

        await Assert.That(Diagnostic(client)).IsNull();
        await Assert.That(Diagnostic(client.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == experimental.MethodName)))
            .IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(client.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == stable.MethodName))).IsNull();
    }

    [Test]
    public async Task Emit_Should_Mark_An_All_Experimental_Routes_Container_Instead_Of_Its_Members()
    {
        var routes = Type<ClassDeclarationSyntax>(SourceEmitter.Emit(await PlanAsync()), "Probes");

        await Assert.That(Diagnostic(routes)).IsEqualTo("OPENCODE001");
        await Assert.That(routes.Members.All(static member => Diagnostic(member) is null)).IsTrue();
    }

    [Test]
    public async Task Emit_Should_Mark_The_Experimental_Route_Template_And_Builder_In_A_Mixed_Container()
    {
        var plan = await PlanAsync(stable: true);
        var routes = Type<ClassDeclarationSyntax>(SourceEmitter.Emit(plan), "Probes");
        var operation = plan.Clients.SelectMany(static value => value.Operations).Single(static operation => operation.OperationId == "probe.get");

        await Assert.That(Diagnostic(routes)).IsNull();
        await Assert.That(Diagnostic(routes.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == operation.RouteMemberName)))
            .IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(routes.Members.OfType<FieldDeclarationSyntax>().Single(field => field.Declaration.Variables.Single().Identifier.ValueText == operation.RouteMemberName + "Template")))
            .IsEqualTo("OPENCODE001");
        await Assert.That(routes.Members.Count(static member => Diagnostic(member) is not null)).IsEqualTo(2);
    }

    [Test]
    public async Task Emit_Should_Mark_Per_Operation_Records_And_An_Experimental_Only_Model()
    {
        var plan = await PlanAsync();
        var operation = plan.Clients.SelectMany(static value => value.Operations).Single();
        var sources = SourceEmitter.Emit(plan);

        await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, operation.Envelope!.ResponseTypeName))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, operation.QueryRequest!.TypeName))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, "ProbeInfo"))).IsEqualTo("OPENCODE001");
        var attribute = ExperimentalAttribute(Type<RecordDeclarationSyntax>(sources, "ProbeInfo"))!;
        await Assert.That(attribute.Name.ToString()).IsEqualTo("System.Diagnostics.CodeAnalysis.Experimental");
        var url = attribute.ArgumentList!.Arguments.Single(static argument => argument.NameEquals?.Name.Identifier.ValueText == "UrlFormat");
        await Assert.That(((LiteralExpressionSyntax)url.Expression).Token.ValueText)
            .IsEqualTo("https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001");
    }

    [Test]
    public async Task Emit_Should_Leave_Internal_Artifacts_Unmarked()
    {
        var sources = SourceEmitter.Emit(await PlanAsync());
        var declarations = sources.SelectMany(static source => Parse(source).DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            .Where(static declaration => declaration.Modifiers.Any(SyntaxKind.InternalKeyword)).ToArray();

        await Assert.That(declarations.Length).IsGreaterThan(0);
        await Assert.That(declarations.All(static declaration => Diagnostic(declaration) is null)).IsTrue();
    }

    [Test]
    public async Task Emit_Should_Mark_The_Public_Artifacts_Of_An_Internal_Raw_Family_But_Not_Its_Raw_Client()
    {
        var plan = await PlanAsync(emission: EmissionMode.InternalRaw);
        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();
        var sources = SourceEmitter.Emit(plan);
        var root = Type<ClassDeclarationSyntax>(sources, "OpenCodeClient");

        await Assert.That(Diagnostic(Type<ClassDeclarationSyntax>(sources, "ProbesRawClient"))).IsNull();
        // The routes, records and models stay public under an internal-raw family, so they carry the id.
        await Assert.That(Diagnostic(Type<ClassDeclarationSyntax>(sources, "Probes"))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, operation.Envelope!.ResponseTypeName))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, operation.QueryRequest!.TypeName))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(root.Members.OfType<PropertyDeclarationSyntax>().Single(static property => property.Identifier.ValueText == "Probes")))
            .IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, "ProbeInfo"))).IsEqualTo("OPENCODE001");
    }

    [Test]
    public async Task Emit_Should_Mark_Public_Enum_And_Union_Types_But_Not_Their_Converters()
    {
        var fixture = EmitterPlanFixture.Create();
        var plan = fixture with
        {
            Models = [.. fixture.Models.Select(static model => model with { ExperimentalDiagnostic = "OPENCODE001" })],
            Unions = [.. fixture.Unions.Select(static union => union with { ExperimentalDiagnostic = "OPENCODE001" })],
        };
        var sources = SourceEmitter.Emit(plan);

        await Assert.That(Diagnostic(Type<StructDeclarationSyntax>(sources, "ExampleMode"))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<InterfaceDeclarationSyntax>(sources, "IExampleEvent"))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, "UnknownExampleEvent"))).IsEqualTo("OPENCODE001");
        await Assert.That(Diagnostic(Type<ClassDeclarationSyntax>(sources, "ExampleEventJsonConverter"))).IsNull();
        await Assert.That(Diagnostic(Type<ClassDeclarationSyntax>(sources, "ExampleModeJsonConverter"))).IsNull();
    }

    [Test]
    public async Task Emit_Should_Mark_A_Structural_Union_And_Its_Kind_But_Not_Its_Converter()
    {
        var plan = await EmitterPlanFixture.CreateStructuralUnionPlanAsync();
        var unions = plan.Models.OfType<StructuralUnionModelPlan>()
            .Select(static union => union with { ExperimentalDiagnostic = "OPENCODE002" }).ToArray();
        var sources = StructuralUnionEmitter.Emit(unions, plan.Placement);

        await Assert.That(unions.Length).IsGreaterThan(0);
        foreach (var union in unions)
        {
            await Assert.That(Diagnostic(Type<RecordDeclarationSyntax>(sources, union.Name))).IsEqualTo("OPENCODE002");
            await Assert.That(Diagnostic(Type<EnumDeclarationSyntax>(sources, union.KindTypeName))).IsEqualTo("OPENCODE002");
            await Assert.That(Diagnostic(Type<ClassDeclarationSyntax>(sources, union.Name + "JsonConverter"))).IsNull();
        }
    }

    [Test]
    public async Task Emit_Should_Mark_A_Hoisted_Interface_From_Its_Plan()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();
        var hoisted = plan.HoistedInterfaces.Single(static value => value.Name == "IDurableEnvelope") with { ExperimentalDiagnostic = "OPENCODE003" };

        var sources = HoistedInterfaceEmitter.Emit([hoisted], plan.Placement);

        await Assert.That(Diagnostic(Type<InterfaceDeclarationSyntax>(sources, hoisted.Name))).IsEqualTo("OPENCODE003");
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Compile_Should_Report_The_Experimental_Diagnostic_Unless_The_Consumer_Suppresses_It()
    {
        var sources = SourceEmitter.Emit(await new BindingTestHost().BindPinnedAsync());
        var consumer = ExperimentalConsumerSyntax.CreateTree();

        var unsuppressed = await GeneratedSourceCompiler.CompileConsumerAsync(sources, consumer, suppressExperimental: false);
        var suppressed = await GeneratedSourceCompiler.CompileConsumerAsync(sources, consumer, suppressExperimental: true);

        await Assert.That(unsuppressed.Any(static diagnostic => diagnostic.Id == "OPENCODE001" && diagnostic.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(suppressed).IsEmpty();
    }

    private static async Task<EmitPlan> PlanAsync(bool stable = false, EmissionMode emission = EmissionMode.Public)
    {
        var document = await BindingTestHost.IngestAsync(new ExperimentalOperationScenario(stable));
        return new BindingTestHost().Bind(document, stable ? Selection("probe.get", "probe.list") : Selection("probe.get"),
            Curation(Groups("probe", ClientGroup("Probes", null, null, emission, "OPENCODE001"))));
    }

    private static T Type<T>(IReadOnlyList<GeneratedSource> sources, string name) where T : BaseTypeDeclarationSyntax =>
        sources.SelectMany(static source => Parse(source).DescendantNodes().OfType<T>())
            .Single(declaration => declaration.Identifier.ValueText == name);

    private static CompilationUnitSyntax Parse(GeneratedSource source) =>
        CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(source.Utf8Source.Span)).GetCompilationUnitRoot();

    private static AttributeSyntax? ExperimentalAttribute(SyntaxNode declaration) => declaration.ChildNodes().OfType<AttributeListSyntax>()
        .SelectMany(static list => list.Attributes).SingleOrDefault(static attribute => attribute.Name.ToString() == "System.Diagnostics.CodeAnalysis.Experimental");

    private static string? Diagnostic(SyntaxNode declaration) => ExperimentalAttribute(declaration) is { } attribute
        ? ((LiteralExpressionSyntax)attribute.ArgumentList!.Arguments[0].Expression).Token.ValueText : null;
}
