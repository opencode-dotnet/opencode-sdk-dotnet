using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// CA1034 yields for every file under a <c>Models</c> folder of the SDK, because each generated
/// extensible enum nests one static <c>Values</c> class of wire constants. The arbitration is a
/// folder glob, so these tests keep it from covering more than that: the glob is exactly the
/// models one, every file under a <c>Models</c> folder is generated, and the only public type a
/// generated model nests, delegates included, is a static <c>Values</c> class of string constants
/// inside an enum struct.
/// </summary>
public sealed class GeneratedModelNestingTests
{
    /// <summary>The section that turns CA1034 off for the generated models.</summary>
    private const string ModelsSection = "[src/OpenCode.Sdk/**/Models/**.cs]";

    /// <summary>A wider glob, or a second SDK section, fails here until someone decides it.</summary>
    [Test]
    public async Task The_CA1034_Arbitration_Should_Cover_The_Generated_Models_And_Nothing_Wider()
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        var lines = await fileSystem.File.ReadAllLinesAsync(fileSystem.Path.Combine(root, ".editorconfig"), CancellationToken.None);

        var sections = new List<string>();
        var current = string.Empty;
        foreach (var line in lines.Select(static line => line.Trim()))
        {
            if (line is ['[', ..])
            {
                current = line;
            }
            else if (line.StartsWith("dotnet_diagnostic.CA1034.severity", StringComparison.Ordinal) && line.EndsWith("none", StringComparison.Ordinal))
            {
                sections.Add(current);
            }
        }

        await Assert.That(sections.Where(static section => section.StartsWith("[src/OpenCode.Sdk", StringComparison.Ordinal)))
            .IsEquivalentTo([ModelsSection]);
    }

    [Test]
    public async Task Every_File_Under_A_Models_Folder_Should_Be_Generated()
    {
        var models = await LoadModelsAsync();

        await Assert.That(models).IsNotEmpty();
        await Assert.That(models.Where(static model => !model.Generated).Select(static model => model.Path)).IsEmpty();
    }

    [Test]
    public async Task The_Only_Public_Type_A_Generated_Model_Nests_Should_Be_An_Enums_Values_Class()
    {
        var models = await LoadModelsAsync();

        var nested = models
            .SelectMany(static model => model.Root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Where(static declaration => declaration is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                                             && declaration.Parent is BaseTypeDeclarationSyntax
                                             && declaration.Modifiers.Any(SyntaxKind.PublicKeyword))
                .Select(declaration => (model.Path, Declaration: declaration)))
            .ToArray();
        var other = nested
            .Where(static entry => !IsValuesClass(entry.Declaration))
            .Select(static entry => entry.Path + ": " + entry.Declaration.ToString());

        await Assert.That(nested).IsNotEmpty();
        await Assert.That(other).IsEmpty();
    }

    /// <summary>A static class named <c>Values</c>, nested in a struct, that declares nothing but public string constants.</summary>
    private static bool IsValuesClass(MemberDeclarationSyntax declaration) =>
        declaration is ClassDeclarationSyntax { Identifier.ValueText: "Values", Parent: StructDeclarationSyntax } values
        && values.Modifiers.Any(SyntaxKind.StaticKeyword)
        && values.Members.All(static member => member is FieldDeclarationSyntax field
                                               && field.Modifiers.Any(SyntaxKind.PublicKeyword)
                                               && field.Modifiers.Any(SyntaxKind.ConstKeyword)
                                               && field.Declaration.Type is PredefinedTypeSyntax { Keyword.ValueText: "string" });

    private static async Task<List<ModelSource>> LoadModelsAsync()
    {
        var fileSystem = new RealFileSystem();
        var root = fileSystem.Path.Combine(AppContext.BaseDirectory, "Fixtures", "SdkSource");
        var models = new List<ModelSource>();
        foreach (var path in fileSystem.Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = fileSystem.Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!relative.Split('/').SkipLast(1).Contains("Models", StringComparer.Ordinal))
            {
                continue;
            }

            var text = await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
            models.Add(new ModelSource(relative, GenerationProvenance.HasHeader(text), await CSharpSyntaxTree.ParseText(text, path: path).GetRootAsync()));
        }

        return models;
    }

    private sealed record ModelSource(string Path, bool Generated, SyntaxNode Root);
}
