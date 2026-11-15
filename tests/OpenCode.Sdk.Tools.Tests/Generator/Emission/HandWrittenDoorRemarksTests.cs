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
/// (<see cref="OperationRemarks"/>); the PTY families' and the Events family's doors are hand-written
/// (ADR-0021) and carry the same remark by hand, so a spec refresh that renames an operation or
/// moves its route would leave them stale if nothing compared them. The comparison runs against
/// the ingested document, identity curation applied, exactly as the generator sees it.
/// Hand-written sources are recognized the way the compile probe recognizes them: generated
/// sources carry the writer's header.
/// </summary>
[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed partial class HandWrittenDoorRemarksTests(PinnedBinding pinned)
{
    /// <summary>The hand-written public door types (ADR-0021): every public operation method on them names its operation.</summary>
    private static readonly string[] DoorTypes = ["PtyClient", "PtysClient", "PersistentPtyClient", "PersistentPtysClient", "EventsClient"];

    [Test]
    public async Task SubscribeAsync_Remarks_Should_Quote_The_Current_Server_Sent_Events_Canon()
    {
        var method = (await LoadHandWrittenMethodsAsync()).Single(static method => method.TypeName == "EventsClient" && method.Name == "SubscribeAsync");
        var fileSystem = new RealFileSystem();
        var path = fileSystem.Path.Combine(new RepositoryRoot(fileSystem).Locate(), "docs/architecture/client-runtime.md");
        var canon = await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
        const string heading = "## Server-sent events";
        var start = canon.IndexOf(heading, StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThanOrEqualTo(0);
        var end = canon.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        var section = Normalize((end < 0 ? canon[start..] : canon[start..end]).Replace("`", "", StringComparison.Ordinal));
        var excerpts = Normalize(method.PlainRemarks).Split(". ", StringSplitOptions.RemoveEmptyEntries)
            .Where(static excerpt => !excerpt.StartsWith("Operation ", StringComparison.Ordinal)).ToArray();
        await Assert.That(excerpts).IsNotEmpty();
        foreach (var excerpt in excerpts)
        {
            await Assert.That(section).Contains(excerpt.TrimEnd('.'));
        }
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Test]
    [Arguments("SubscribeAsync", true, 1)]
    [Arguments("WatchConnectionAsync", false, 0)]
    [Arguments("GetConnectionState", false, 0)]
    public async Task Classify_Should_Treat_Only_The_Events_Subscription_As_An_Http_Operation(string name, bool operation, int remarks)
    {
        var method = (await LoadHandWrittenMethodsAsync()).Single(method => method.TypeName == "EventsClient" && method.Name == name);

        await Assert.That(method.IsPublicOperation).IsEqualTo(operation);
        await Assert.That(method.Remarks.Count).IsEqualTo(remarks);
    }

    [Test]
    public async Task HandWritten_Remarks_Should_Name_A_Pinned_Operation_By_Its_Method_And_Route()
    {
        var document = pinned.Document;
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

    private sealed record HandWrittenMethod(string TypeName, string Name, bool IsPublicOperation,
        IReadOnlyList<OperationRemark> Remarks, string PlainRemarks)
    {
        public static HandWrittenMethod Of(MethodDeclarationSyntax method)
        {
            var typeName = ((TypeDeclarationSyntax)method.Parent!).Identifier.ValueText;
            var name = method.Identifier.ValueText;
            var remarkElements = method
                .GetLeadingTrivia()
                .Select(static trivia => trivia.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .SelectMany(static comment => comment.Content.OfType<XmlElementSyntax>())
                .Where(static element => element.StartTag.Name.LocalName.ValueText == "remarks").ToArray();
            var remarkText = string.Concat(remarkElements.Select(static element => element.Content.ToString()));
            var plainRemarks = string.Concat(remarkElements.SelectMany(static element => element.DescendantNodes().OfType<XmlTextSyntax>())
                .SelectMany(static text => text.TextTokens).Select(static token => token.ValueText));
            var remarks = RemarkPattern()
                .Matches(remarkText)
                .Select(match => new OperationRemark(
                    $"{typeName}.{name}",
                    match.Groups["id"].Value,
                    match.Groups["method"].Value,
                    match.Groups["route"].Value))
                .ToArray();
            var isPublicOperation = method.Modifiers.Any(SyntaxKind.PublicKeyword) && (typeName == "EventsClient"
                ? name switch
                {
                    "SubscribeAsync" => true,
                    "WatchConnectionAsync" or "GetConnectionState" => false,
                    _ => throw new InvalidOperationException($"Unclassified Events door member: {name}"),
                }
                : method.ReturnType is GenericNameSyntax { Identifier.ValueText: "Task" or "IAsyncEnumerable" });
            return new HandWrittenMethod(typeName, name, isPublicOperation, remarks, plainRemarks);
        }
    }
}
