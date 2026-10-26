using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using OpenCode.Sdk.Tools.Generator.Emission;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class EmissionSyntaxTests
{
    [Test]
    [Arguments("Widget", "class Widget\n{\n}\n")]
    [Arguments("Café", "class Café\n{\n}\n")]
    public async Task CreateSource_Should_Preserve_Utf8_And_Final_Newline(string name, string expectedDeclaration)
    {
        var unit = EmissionSyntax.CompilationUnit("Example", [], [SyntaxFactory.ClassDeclaration(name)]);

        var source = EmissionSyntax.CreateSource("Example.cs", unit);

        var expected = Encoding.UTF8.GetBytes(GenerationProvenance.Header + "namespace Example;\n" + expectedDeclaration);
        await Assert.That(source.Utf8Source.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching)
            .Because(Encoding.UTF8.GetString(source.Utf8Source.Span));
    }
}
