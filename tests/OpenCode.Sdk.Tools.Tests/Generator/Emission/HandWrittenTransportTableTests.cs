using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// The transport's hand-written per-operation table names operations by id, and nothing else ties
/// those ids to the pinned document: an operation that a refresh renames or removes would leave a
/// row that silently matches nothing, and its long wait would fall back to the ordinary window, or
/// its stream would stop reconnecting. Every id the table names is an operation of the pinned
/// document, identity curation applied.
/// </summary>
[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class HandWrittenTransportTableTests(PinnedBinding pinnedBinding)
{
    [Test]
    [Arguments("Internal/Operations/OperationTraits.cs", "TraitsByOperation")]
    public async Task Every_Table_Id_Should_Name_A_Pinned_Operation(string tablePath, string tableField)
    {
        var document = pinnedBinding.Document;
        var pinned = document.Operations.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);

        var ids = await TableIdsAsync(tablePath, tableField);

        await Assert.That(ids).IsNotEmpty();
        await Assert.That(ids.Where(id => !pinned.Contains(id))).IsEmpty();
        await Assert.That(ids).IsEquivalentTo(
        [
            "event.subscribe",
            "experimental.generate.text",
            "experimental.session.wait",
            "session.command",
            "session.generate",
            "session.shell",
        ], CollectionOrdering.Any);
    }

    private static async Task<string[]> TableIdsAsync(string tablePath, string tableField)
    {
        var fileSystem = new RealFileSystem();
        var path = fileSystem.Path.Combine(AppContext.BaseDirectory, "Fixtures", "SdkSource", tablePath);
        var text = await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
        var root = await CSharpSyntaxTree.ParseText(text, path: path).GetRootAsync();
        var field = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == tableField);

        // Every key is read as a string literal; one written any other way (a constant, an
        // interpolation) would escape the pinned-id check, so it fails here instead.
        var entries = field.DescendantNodes().OfType<InitializerExpressionSyntax>().Single().Expressions;
        var unread = entries.Where(static entry => entry is not AssignmentExpressionSyntax
        {
            Left: ImplicitElementAccessSyntax
            {
                ArgumentList.Arguments:
                [
                { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } },
                ],
            },
        }).Select(static entry => entry.ToString()).ToArray();
        if (unread.Length > 0)
        {
            throw new InvalidOperationException($"The table {tableField} holds entries that are not string literals: {string.Join(", ", unread)}");
        }

        return [.. entries.Cast<AssignmentExpressionSyntax>()
            .Select(static entry => ((LiteralExpressionSyntax)((ImplicitElementAccessSyntax)entry.Left).ArgumentList.Arguments[0].Expression).Token.ValueText)];
    }
}
