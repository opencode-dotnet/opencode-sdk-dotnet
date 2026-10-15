using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Emission;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// CA1034 yields for every file under a <c>Models</c> folder of the SDK, because each generated
/// extensible enum nests one static <c>Values</c> class of wire constants. The arbitration is a
/// folder glob, so these tests keep it from covering more than that: every file under a
/// <c>Models</c> folder is generated, and the only public type a generated model nests is that
/// <c>Values</c> class, inside an enum struct.
/// </summary>
public sealed class GeneratedModelNestingTests
{
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
            .SelectMany(static model => model.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Where(static declaration => declaration.Parent is BaseTypeDeclarationSyntax && declaration.Modifiers.Any(SyntaxKind.PublicKeyword))
                .Select(declaration => (model.Path, Declaration: declaration)))
            .ToArray();
        var other = nested
            .Where(static entry => entry.Declaration is not ClassDeclarationSyntax { Identifier.ValueText: "Values", Parent: StructDeclarationSyntax })
            .Select(static entry => $"{entry.Path}: {entry.Declaration.Identifier.ValueText}");

        await Assert.That(nested).IsNotEmpty();
        await Assert.That(other).IsEmpty();
    }

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
