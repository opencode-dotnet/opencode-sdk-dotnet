using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// The hand-written doors over an internal-raw family whose every operation is experimental carry
/// the id the generator gives the family's generated members. The generator cannot write them, so
/// nothing else keeps the two in step: every public type the family's folder declares by hand
/// carries the attribute, and the id it names resolves to the one the family's curation row holds.
/// </summary>
[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class HandWrittenExperimentalDoorTests(PinnedBinding pinned)
{
    [Test]
    public async Task HandWritten_Public_Types_Of_An_Experimental_Raw_Family_Should_Carry_Its_Id()
    {
        var curation = pinned.Curation;
        var document = OperationGroupPolicy.Apply(pinned.Document, curation.Groups);
        var families = curation.Groups
            .Where(pair => pair.Value is { Emission: EmissionMode.InternalRaw, ExperimentalDiagnostic: not null, ClientName: not null }
                           && document.Operations
                               .Where(operation => string.Equals(operation.Group, pair.Key, StringComparison.Ordinal))
                               .All(static operation => operation.Path.StartsWith("/api/experimental/", StringComparison.Ordinal)))
            .Select(static pair => (Folder: pair.Value.ClientName!, Id: pair.Value.ExperimentalDiagnostic!))
            .ToArray();
        var sources = await LoadHandWrittenAsync();
        var constants = Constants(sources);

        var findings = new List<string>();
        var checkedTypes = 0;
        foreach (var (folder, id) in families)
        {
            foreach (var type in sources
                         .Where(source => string.Equals(source.Folder, folder, StringComparison.Ordinal))
                         .SelectMany(static source => source.Root.Members.OfType<BaseNamespaceDeclarationSyntax>().SelectMany(static space => space.Members))
                         .OfType<BaseTypeDeclarationSyntax>()
                         .Where(static type => type.Modifiers.Any(SyntaxKind.PublicKeyword)))
            {
                checkedTypes++;
                var named = ExperimentalId(type, constants);
                if (!string.Equals(named, id, StringComparison.Ordinal))
                {
                    findings.Add($"{folder}/{type.Identifier.ValueText} carries {named ?? "no experimental id"}; its family's row says {id}");
                }
            }
        }

        await Assert.That(families).IsNotEmpty();
        await Assert.That(checkedTypes).IsGreaterThan(0);
        await Assert.That(findings).IsEmpty();
    }

    /// <summary>The attribute's id, read from a literal or from a string constant the hand-written sources declare.</summary>
    private static string? ExperimentalId(BaseTypeDeclarationSyntax type, IReadOnlyDictionary<string, string> constants)
    {
        var argument = type.AttributeLists
            .SelectMany(static list => list.Attributes)
            .Where(static attribute => attribute.Name.ToString() is "Experimental" or "System.Diagnostics.CodeAnalysis.Experimental")
            .Select(static attribute => attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .FirstOrDefault();
        return argument switch
        {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            MemberAccessExpressionSyntax access => constants.GetValueOrDefault(access.ToString()),
            _ => null,
        };
    }

    /// <summary>Every string constant the hand-written sources declare, keyed as <c>Type.Name</c>.</summary>
    private static Dictionary<string, string> Constants(IEnumerable<HandWrittenSource> sources) =>
        sources
            .SelectMany(static source => source.Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            .Where(static field => field.Modifiers.Any(SyntaxKind.ConstKeyword) && field.Parent is TypeDeclarationSyntax)
            .SelectMany(static field => field.Declaration.Variables
                .Where(static variable => variable.Initializer?.Value is LiteralExpressionSyntax)
                .Select(variable => (
                    Key: $"{((TypeDeclarationSyntax)field.Parent!).Identifier.ValueText}.{variable.Identifier.ValueText}",
                    Value: ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText)))
            .GroupBy(static constant => constant.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Value, StringComparer.Ordinal);

    private static async Task<List<HandWrittenSource>> LoadHandWrittenAsync()
    {
        var fileSystem = new RealFileSystem();
        var root = fileSystem.Path.Combine(AppContext.BaseDirectory, "Fixtures", "SdkSource");
        var sources = new List<HandWrittenSource>();
        foreach (var path in fileSystem.Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
            if (GenerationProvenance.HasHeader(text))
            {
                continue;
            }

            var folder = fileSystem.Path.GetRelativePath(root, fileSystem.Path.GetDirectoryName(path)!).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(text, path: path);
            sources.Add(new HandWrittenSource(folder, (CompilationUnitSyntax)await tree.GetRootAsync()));
        }

        return sources;
    }

    private sealed record HandWrittenSource(string Folder, CompilationUnitSyntax Root);
}
