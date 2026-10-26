using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class EnvelopeEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_Guarded_Response_Envelopes()
    {
        var sources = EnvelopeEmitter.Emit(EmitterPlanFixture.Create().Clients, EmitterPlanFixture.Create().Namespaces);

        await Verify(EmitterSnapshot.Create(sources));
    }

    [Test]
    [Arguments("WidgetListResponse", "Widgets", "Cursor")]
    [Arguments("WidgetSpotResponse", "Spot", "Location")]
    public async Task Emit_Should_Print_Envelope_Owned_Members_After_The_Payload(string response, string payload, string member)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(member);
        var plan = EmitterPlanFixture.Create();
        var root = await CSharpSyntaxTree.ParseText(EmitterSnapshot.Content(EnvelopeEmitter.Emit(plan.Clients, plan.Namespaces), $"Widgets/{response}.cs")).GetRootAsync();
        var method = root.DescendantNodes().OfType<RecordDeclarationSyntax>().Single(record => record.Identifier.ValueText == response)
            .Members.OfType<MethodDeclarationSyntax>().Single();
        var expected = $$"""
            protected override bool PrintMembers(StringBuilder builder)
            {
                ArgumentNullException.ThrowIfNull(builder);
                var printed = base.PrintMembers(builder);
                if (_{{payload.ToLowerInvariant()}} is null)
                {
                    return printed;
                }

                if (printed)
                {
                    _ = builder.Append(", ");
                }

                _ = builder.Append("{{payload}} = ").Append(_{{payload.ToLowerInvariant()}});
                _ = builder.Append(", {{member}} = ").Append(_{{member.ToLowerInvariant()}});
                return true;
            }
            """;

        await Assert.That(method.WithoutTrivia().NormalizeWhitespace(eol: "\n").ToFullString()).IsEqualTo(expected);
    }
}
