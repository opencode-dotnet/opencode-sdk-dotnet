using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

public sealed class GeneratedTypeCatalogTests
{
    [Test]
    [Arguments("public sealed class AgentInfo { }", "AgentInfo")]
    [Arguments("public abstract record AgentInfo;", "AgentInfo")]
    [Arguments("public readonly record struct AgentInfo;", "AgentInfo")]
    [Arguments("internal readonly ref struct AgentInfo { }", "AgentInfo")]
    [Arguments("internal partial interface AgentInfo { }", "AgentInfo")]
    [Arguments("public enum AgentInfo { }", "AgentInfo")]
    [Arguments("public delegate void AgentInfo<T>(T value);", "AgentInfo`1")]
    public async Task ReadDeclarations_Should_Read_Every_Top_Level_Type_Kind(string declaration, string expectedName)
    {
        var source = SyntaxFactory.CompilationUnit()
            .AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName("OpenCode.Sdk.Models"))
                .AddMembers(SyntaxFactory.ParseMemberDeclaration(declaration)!))
            .NormalizeWhitespace().ToFullString();

        var types = new GeneratedTypeCatalog().ReadDeclarations(source, "Models/UnrelatedFilename.cs", generated: true);

        await Assert.That(types.Single().FullName).IsEqualTo("OpenCode.Sdk.Models." + expectedName);
    }

    [Test]
    public async Task ReadDeclarations_Should_Ignore_Nested_Declarations_In_The_Generated_Catalog()
    {
        var nested = SyntaxFactory.ClassDeclaration("Nested");
        var outer = SyntaxFactory.ClassDeclaration("AgentInfo").AddMembers(nested);
        var source = SyntaxFactory.CompilationUnit()
            .AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName("OpenCode.Sdk.Models")).AddMembers(outer))
            .NormalizeWhitespace().ToFullString();

        var types = new GeneratedTypeCatalog().ReadDeclarations(source, "Models/AgentInfo.cs", generated: true);

        await Assert.That(types.Select(static type => type.FullName)).IsEquivalentTo(["OpenCode.Sdk.Models.AgentInfo"]);
    }
}
