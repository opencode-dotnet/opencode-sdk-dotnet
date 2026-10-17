using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// Keeps the operation remark on the hand-written public doors in step with the pinned document.
/// The generator writes the remark on every generated member from the ingested operation
/// (<see cref="OperationRemarks"/>); the normal and persistent PTY doors are hand-written
/// (ADR-0021) and carry the same remark by hand, so a spec refresh that renames an operation or
/// moves its route would leave them stale if nothing compared them. The comparison runs against
/// the ingested document, identity curation applied, exactly as the generator sees it.
/// Hand-written sources are recognized the way the compile probe recognizes them: generated
/// sources carry the writer's header.
/// </summary>
public sealed partial class HandWrittenDoorRemarksTests
{
    /// <summary>The hand-written public door types (ADR-0021): every public operation method on them names its operation.</summary>
    private static readonly string[] DoorTypes = ["PtyClient", "PtysClient", "PersistentPtyClient", "PersistentPtysClient"];

    [Test]
    public async Task HandWritten_Remarks_Should_Name_A_Pinned_Operation_By_Its_Method_And_Route()
    {
        var document = await BindingTestHost.IngestPinnedAsync();
        var operations = document.Operations.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        var remarks = (await LoadHandWrittenMethodsAsync()).SelectMany(static method => method.Remarks).ToArray();

        var stale = remarks
            .Where(remark => !operations.TryGetValue(remark.OperationId, out var operation)
                             || !string.Equals(operation.Method.ToUpperInvariant(), remark.Method, StringComparison.Ordinal)
                             || !string.Equals(operation.Path, remark.Route, StringComparison.Ordinal))
            .Select(remark => operations.TryGetValue(remark.OperationId, out var operation)
                ? $"{remark.Member} names {remark.OperationId} as {remark.Method} {remark.Route}; the pinned document says {operation.Method.ToUpperInvariant()} {operation.Path}"
                : $"{remark.Member} names {remark.OperationId}, which the pinned document does not declare")
            .ToArray();

        await Assert.That(remarks).IsNotEmpty();
        await Assert.That(stale).IsEmpty();
    }

    [Test]
    public async Task Every_HandWritten_Door_Should_Carry_Exactly_One_Operation_Remark()
    {
        var doors = (await LoadHandWrittenMethodsAsync())
            .Where(static method => DoorTypes.Contains(method.TypeName, StringComparer.Ordinal) && method.IsPublicOperation)
            .ToArray();

        var unmarked = doors
            .Where(static door => door.Remarks.Count != 1)
            .Select(static door => $"{door.TypeName}.{door.Name} carries {door.Remarks.Count} operation remarks")
            .ToArray();

        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(doors.Select(static door => door.TypeName).Distinct(StringComparer.Ordinal))
            .IsEquivalentTo(DoorTypes, CollectionOrdering.Any);
        await Assert.That(unmarked).IsEmpty();
    }

    // Reads the repository's own hand-written door remarks, never untrusted input; a wall-clock match
    // timeout would only fail it on a stalled CI runner.
    [GeneratedRegex(
        @"Operation <c>(?<id>[^<]+)</c>: <c>(?<method>[A-Z]+) (?<route>[^<]+)</c>\.",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex RemarkPattern();

    private static async Task<List<HandWrittenMethod>> LoadHandWrittenMethodsAsync()
    {
        var fileSystem = new RealFileSystem();
        var root = fileSystem.Path.Combine(AppContext.BaseDirectory, "Fixtures", "SdkSource");
        var methods = new List<HandWrittenMethod>();
        foreach (var path in fileSystem.Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
            if (GenerationProvenance.HasHeader(text))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: path);
            var compilationUnit = await tree.GetRootAsync();
            methods.AddRange(compilationUnit
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Parent is TypeDeclarationSyntax)
                .Select(static method => HandWrittenMethod.Of(method)));
        }

        return methods;
    }

    private sealed record OperationRemark(string Member, string OperationId, string Method, string Route);

    private sealed record HandWrittenMethod(string TypeName, string Name, bool IsPublicOperation, IReadOnlyList<OperationRemark> Remarks)
    {
        public static HandWrittenMethod Of(MethodDeclarationSyntax method)
        {
            var typeName = ((TypeDeclarationSyntax)method.Parent!).Identifier.ValueText;
            var name = method.Identifier.ValueText;
            var remarkText = string.Concat(method
                .GetLeadingTrivia()
                .Select(static trivia => trivia.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .SelectMany(static comment => comment.Content.OfType<XmlElementSyntax>())
                .Where(static element => element.StartTag.Name.LocalName.ValueText == "remarks")
                .Select(static element => element.Content.ToString()));
            var remarks = RemarkPattern()
                .Matches(remarkText)
                .Select(match => new OperationRemark(
                    $"{typeName}.{name}",
                    match.Groups["id"].Value,
                    match.Groups["method"].Value,
                    match.Groups["route"].Value))
                .ToArray();
            var isPublicOperation = method.Modifiers.Any(SyntaxKind.PublicKeyword)
                                    && method.ReturnType is GenericNameSyntax { Identifier.ValueText: "Task" };
            return new HandWrittenMethod(typeName, name, isPublicOperation, remarks);
        }
    }
}
