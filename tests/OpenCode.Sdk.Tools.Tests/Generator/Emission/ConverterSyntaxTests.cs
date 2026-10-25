using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OpenCode.Sdk.Tools.Generator.Emission;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ConverterSyntaxTests
{
    [Test]
    [Arguments("typeToConvert,options", "", "/// <summary>\n/// Preserves converter members.\n/// </summary>\ninternal sealed class SampleJsonConverter : JsonConverter<Sample>\n{\n    public override bool HandleNull => true;\n\n    public override Sample Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)\n    {\n        ArgumentNullException.ThrowIfNull(typeToConvert);\n        ArgumentNullException.ThrowIfNull(options);\n        return default;\n    }\n\n    public override void Write(Utf8JsonWriter writer, Sample value, JsonSerializerOptions options)\n    {\n        writer.WriteNullValue();\n    }\n}")]
    [Arguments("", "writer,value,options", "/// <summary>\n/// Preserves converter members.\n/// </summary>\ninternal sealed class SampleJsonConverter : JsonConverter<Sample>\n{\n    public override bool HandleNull => true;\n\n    public override Sample Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)\n    {\n        return default;\n    }\n\n    public override void Write(Utf8JsonWriter writer, Sample value, JsonSerializerOptions options)\n    {\n        ArgumentNullException.ThrowIfNull(writer);\n        ArgumentNullException.ThrowIfNull(value);\n        ArgumentNullException.ThrowIfNull(options);\n        writer.WriteNullValue();\n    }\n}")]
    [Arguments("", "writer", "/// <summary>\n/// Preserves converter members.\n/// </summary>\ninternal sealed class SampleJsonConverter : JsonConverter<Sample>\n{\n    public override bool HandleNull => true;\n\n    public override Sample Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)\n    {\n        return default;\n    }\n\n    public override void Write(Utf8JsonWriter writer, Sample value, JsonSerializerOptions options)\n    {\n        ArgumentNullException.ThrowIfNull(writer);\n        writer.WriteNullValue();\n    }\n}")]
    public async Task Emit_Should_Preserve_Skeletons_And_Guard_Order(string readGuards, string writeGuards, string expected)
    {
        ArgumentNullException.ThrowIfNull(readGuards);
        ArgumentNullException.ThrowIfNull(writeGuards);
        var type = SyntaxFactory.IdentifierName("Sample");
        var handleNull = SyntaxFactory.PropertyDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "HandleNull")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var read = ConverterSyntax.Read(type, SyntaxFactory.Block(SyntaxFactory.ReturnStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))), readGuards.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var write = ConverterSyntax.Write(type, SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("writer"), SyntaxFactory.IdentifierName("WriteNullValue"))))),
            writeGuards.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var declaration = ConverterSyntax.Declaration("SampleJsonConverter", type, [handleNull, read, write],
            DocumentationSyntax.Documentation("Preserves converter members."));
        await Assert.That(declaration.NormalizeWhitespace(indentation: "    ", eol: "\n", elasticTrivia: false).ToFullString()).IsEqualTo(expected);
    }
}
