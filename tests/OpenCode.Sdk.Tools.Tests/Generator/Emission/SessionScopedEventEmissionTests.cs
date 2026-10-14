using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class SessionScopedEventEmissionTests
{
    [Test]
    public async Task Emit_Should_Add_The_Interface_And_An_Explicit_Payload_Projection()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());
        var record = Record(ModelEmitter.Emit(plan), "Qualified");
        var baseList = record.BaseList!;

        await Assert.That(baseList.Types.Select(static type => type.Type.ToString())).Contains("ISessionScopedEvent");
        await Assert.That(baseList.Types.Count(static type => type.Type.ToString() == "ISessionScopedEvent")).IsEqualTo(1);
        var member = record.Members.OfType<PropertyDeclarationSyntax>()
            .Single(static property => property.ExplicitInterfaceSpecifier?.Name.ToString() == "ISessionScopedEvent");
        await Assert.That(member.Type.ToString()).IsEqualTo("string");
        await Assert.That(member.Identifier.ValueText).IsEqualTo("SessionId");
        await Assert.That(member.ExpressionBody!.Expression.ToString()).IsEqualTo("Data.SessionId");
        await Assert.That(member.Modifiers).IsEmpty();
        await Assert.That(member.AttributeLists).IsEmpty();
        await Assert.That(record.Members.OfType<PropertyDeclarationSyntax>()
            .Any(static property => property.Identifier.ValueText == "SessionId" && property.ExplicitInterfaceSpecifier is null)).IsFalse();
    }

    [Test]
    public async Task Emit_Should_Leave_A_Non_Qualifying_Record_Unchanged()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var host = new BindingTestHost();
        var withRule = host.Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());
        var withoutRule = host.Bind(document, Selection("event.get"), Curation(Groups("event", RootGroup())));

        await Assert.That(Record(ModelEmitter.Emit(withRule), "Plain").ToFullString())
            .IsEqualTo(Record(ModelEmitter.Emit(withoutRule), "Plain").ToFullString());
        await Assert.That(withRule.Registry.TypeNames).DoesNotContain("ISessionScopedEvent");
    }

    [Test]
    [Arguments("UnknownEvent")]
    [Arguments("UnknownEventLog")]
    public async Task Emit_Should_Not_Project_A_Session_From_An_Unknown_Carrier(string name)
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());
        var carrier = Record(SourceEmitter.Emit(plan), name);

        await Assert.That(carrier.BaseList!.Types.Select(static type => type.Type.ToString())).DoesNotContain("ISessionScopedEvent");
        await Assert.That(carrier.Members.OfType<PropertyDeclarationSyntax>()
            .Any(static property => property.ExplicitInterfaceSpecifier?.Name.ToString() == "ISessionScopedEvent")).IsFalse();
    }

    [Test]
    public async Task Emit_Should_Read_The_Bound_Member_Names_Without_Reinterpreting_The_Rule()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());
        var qualified = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Qualified");
        var renamed = qualified with
        {
            SessionScopedEvent = qualified.SessionScopedEvent! with
            {
                InterfaceName = "IScopedNotice",
                PayloadPropertyName = "Payload",
                IdentifierPropertyName = "Key",
            },
        };
        var sources = ModelEmitter.Emit(plan with
        {
            Models = [.. plan.Models.Select(model => model.Name == renamed.Name ? renamed : model)],
        });

        var record = Record(sources, "Qualified");
        await Assert.That(record.BaseList!.Types.Select(static type => type.Type.ToString())).Contains("IScopedNotice");
        await Assert.That(record.Members.OfType<PropertyDeclarationSyntax>()
            .Single(static property => property.ExplicitInterfaceSpecifier?.Name.ToString() == "IScopedNotice")
            .ExpressionBody!.Expression.ToString()).IsEqualTo("Payload.Key");
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Compile_The_Pinned_Surface_And_A_Session_Routing_Consumer()
    {
        var sources = SourceEmitter.Emit(await new BindingTestHost().BindPinnedAsync());

        var generatedDiagnostics = await GeneratedSourceCompiler.CompileWithSdkCoreAsync(sources);
        var consumerDiagnostics = await GeneratedSourceCompiler.CompileConsumerAsync(
            sources, SessionScopedEventConsumerSyntax.CreateTree(), suppressExperimental: true);

        await Assert.That(generatedDiagnostics).IsEmpty();
        await Assert.That(consumerDiagnostics).IsEmpty();
    }

    private static RecordDeclarationSyntax Record(IReadOnlyList<GeneratedSource> sources, string name) => sources
        .SelectMany(static source => CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(source.Utf8Source.Span))
            .GetCompilationUnitRoot().DescendantNodes().OfType<RecordDeclarationSyntax>())
        .Single(record => record.Identifier.ValueText == name);
}
