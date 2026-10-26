using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ResponseAdapterEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_Status_Mapped_Adapters()
    {
        var sources = ResponseAdapterEmitter.Emit(EmitterPlanFixture.CreateClientPlans(), EmitterPlanFixture.Create().Namespaces);

        await Verify(EmitterSnapshot.Create(sources));
    }

    [Test]
    public async Task Emit_Should_Document_Binary_AdaptSuccess_Refusal()
    {
        var plan = EmitterPlanFixture.Create();
        var client = plan.Clients[0];
        var operation = client.Operations[0];
        var binary = operation with
        {
            Envelope = operation.Envelope! with
            {
                Kind = EnvelopeKind.Binary,
                PayloadName = "Content",
                PayloadType = new BinaryTypeReferencePlan { IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull },
            },
        };
        var sources = ResponseAdapterEmitter.Emit([client with { Operations = [binary] }], plan.Namespaces);
        var method = CSharpSyntaxTree.ParseText(EmitterSnapshot.Create(sources)).GetCompilationUnitRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "AdaptSuccess");
        await Assert.That(method.GetLeadingTrivia().ToFullString().Trim()).IsEqualTo(
            "/// <summary>\n    /// Refuses, because a binary success is adapted from its raw bytes, not from text.\n    /// </summary>");
    }
}
