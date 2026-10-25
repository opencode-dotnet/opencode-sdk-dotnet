using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator;

/// <summary>Reads the C# sources under Generator/ and Output/; a required member moved out of those folders is no longer checked.</summary>
public sealed class RequiredCollectionInitializerTests
{
    [Test]
    public async Task Required_Members_Should_Not_Allocate_Replaced_Initial_Values()
    {
        var fileSystem = new RealFileSystem();
        var root = fileSystem.Path.Combine(new RepositoryRoot(fileSystem).Locate(), "tools", "OpenCode.Sdk.Tools");
        var initializers = new List<string>();
        foreach (var folder in new[] { "Generator", "Output" })
        {
            var paths = fileSystem.Directory.GetFiles(fileSystem.Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories);
            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                var tree = CSharpSyntaxTree.ParseText(await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None), path: path);
                var properties = (await tree.GetRootAsync()).DescendantNodes().OfType<PropertyDeclarationSyntax>();
                initializers.AddRange(properties.Where(static property => property.Initializer is not null
                        && property.Modifiers.Any(SyntaxKind.RequiredKeyword))
                    .Select(property => string.Create(CultureInfo.InvariantCulture, $"{fileSystem.Path.GetRelativePath(root, path)}:{property.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {property.Identifier.ValueText}")));
            }
        }

        await Assert.That(initializers).IsEmpty().Because(string.Join('\n', initializers));
    }

    [Test]
    public async Task A_Required_List_Should_Keep_Its_Own_Copy_Of_The_Assigned_Value()
    {
        var caller = new List<string> { "zeta.get", "alpha.get" };
        var selection = new OperationSelection { OperationIds = caller };

        caller[0] = "changed.get";

        await Assert.That(selection.OperationIds).IsEquivalentTo(["zeta.get", "alpha.get"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_Required_Dictionary_Should_Keep_Its_Own_Copy_Of_The_Assigned_Value()
    {
        var home = TypeHome.Family("Sessions");
        var caller = new Dictionary<string, TypeHome>(StringComparer.Ordinal) { ["Example"] = home };
        var placement = ModelPlacement.Empty with { Homes = caller };

        caller["Example"] = TypeHome.Family("Events");

        await Assert.That(placement.Homes).IsEquivalentTo([KeyValuePair.Create("Example", home)], CollectionOrdering.Matching);
    }
}
