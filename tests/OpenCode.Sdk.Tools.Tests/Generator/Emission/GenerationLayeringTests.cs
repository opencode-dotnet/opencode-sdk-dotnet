using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class GenerationLayeringTests
{
    [Test]
    public async Task Emission_Should_Reference_Only_Plans_And_Approved_Policies()
    {
        string[] approved =
        [
            "ReservedNamePolicy", "CSharpNamePolicy", "ExtensibleEnumMemberPolicy",
            "EffectStreamTypeNamePolicy", "ErrorMarkerPolicy", "TypeReferenceNamePolicy", "GeneratedDisplayName",
        ];
        var compilation = await LoadEmissionAsync();
        var plan = compilation.GetTypeByMetadataName("OpenCode.Sdk.Tools.Generator.Binding.Models.EmitPlan")
            ?? throw new InvalidOperationException("The compilation does not contain EmitPlan.");
        var plans = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        AddPlanTypes(plan, plans, plan.ContainingNamespace);
        var roots = await Task.WhenAll(compilation.SyntaxTrees.Select(static tree => tree.GetRootAsync()));
        var references = roots.SelectMany(root => root.DescendantNodes().OfType<SimpleNameSyntax>()
            .Select(node => compilation.GetSemanticModel(root.SyntaxTree).GetSymbolInfo(node).Symbol)
            .Select(static symbol => symbol as INamedTypeSymbol ?? symbol?.ContainingType))
            .OfType<INamedTypeSymbol>();
        var forbidden = references.Where(type =>
                (type.ContainingNamespace.ToDisplayString().StartsWith("OpenCode.Sdk.Tools.Generator.Binding", StringComparison.Ordinal)
                  && !plans.Contains(type)
                 && !approved.Any(name => string.Equals(type.ToDisplayString(), "OpenCode.Sdk.Tools.Generator.Binding." + name, StringComparison.Ordinal)))
                || (type.ContainingNamespace.ToDisplayString().StartsWith("OpenCode.Sdk.Tools.Generator.Ingestion", StringComparison.Ordinal)
                    && !string.Equals(type.ToDisplayString(), "OpenCode.Sdk.Tools.Generator.Ingestion.Models.LiteralKind", StringComparison.Ordinal)))
            .Select(static type => type.ToDisplayString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        await Assert.That(forbidden).IsEmpty();
    }

    [Test]
    public async Task Emission_Should_Read_Resolved_Chains()
    {
        var compilation = await LoadEmissionAsync();
        var roots = await Task.WhenAll(compilation.SyntaxTrees.Select(static tree => tree.GetRootAsync()));
        var parentReads = roots.SelectMany(root => root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(node => compilation.GetSemanticModel(root.SyntaxTree).GetSymbolInfo(node).Symbol is IPropertySymbol
            { Name: "BaseTypeName", ContainingType.Name: "UnionPlan" })
            .Select(static node => node.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText)).ToArray();
        await Assert.That(parentReads).IsEquivalentTo(["EmitBase", "EmitBase"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Registry_Should_Have_One_Name_Owner()
    {
        var roots = await Task.WhenAll((await LoadEmissionAsync()).SyntaxTrees.Select(static tree => tree.GetRootAsync()));
        var owners = roots.SelectMany(root => root.DescendantTokens()
                .Where(static token => token.IsKind(SyntaxKind.StringLiteralToken) && string.Equals(token.ValueText, "OpenCodeJsonContext", StringComparison.Ordinal))
                .Select(token => root.FindNode(token.Span).AncestorsAndSelf().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText))
            .Order(StringComparer.Ordinal).ToArray();
        await Assert.That(owners).IsEquivalentTo(["RegistryNames"], CollectionOrdering.Matching).Because(string.Join(", ", owners));
    }

    private static async Task<CSharpCompilation> LoadEmissionAsync()
    {
        var fileSystem = new RealFileSystem();
        var root = fileSystem.Path.Combine(new RepositoryRoot(fileSystem).Locate(), "tools", "OpenCode.Sdk.Tools", "Generator", "Emission");
        var trees = await Task.WhenAll(fileSystem.Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(async path => CSharpSyntaxTree.ParseText(await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None), path: path)));
        var assemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        return CSharpCompilation.Create("OpenCode.Sdk.Tools.Tests", trees.Append(CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.Linq;")),
            assemblies.Split(fileSystem.Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path)));
    }

    private static void AddPlanTypes(ITypeSymbol type, HashSet<ITypeSymbol> plans, INamespaceSymbol models)
    {
        if (!plans.Add(type))
        {
            return;
        }

        var carried = type switch
        {
            IArrayTypeSymbol array => Enumerable.Repeat(array.ElementType, 1),
            INamedTypeSymbol named => named.TypeArguments
                .Concat(SymbolEqualityComparer.Default.Equals(named.ContainingNamespace, models)
                    ? named.GetMembers().OfType<IPropertySymbol>().Where(static property => property.DeclaredAccessibility == Accessibility.Public)
                        .Select(static property => property.Type) : [])
                .Concat(models.GetTypeMembers().Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.BaseType, named))),
            _ => [],
        };
        foreach (var child in carried)
        {
            AddPlanTypes(child, plans, models);
        }
    }
}
