using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// The transport's hand-written per-operation table names operations by id, and nothing else ties
/// those ids to the pinned document: an operation that a refresh renames or removes would leave a
/// row that silently matches nothing, and its long wait would fall back to the ordinary window.
/// Every id the table names is an operation of the pinned document, identity curation applied.
/// </summary>
public sealed class HandWrittenTransportTableTests
{
    private const string TablePath = "Internal/Transport/NetworkTimeouts.cs";

    private const string TableField = "LongPollOperations";

    [Test]
    public async Task Every_Long_Poll_Id_Should_Name_A_Pinned_Operation()
    {
        var (document, _, _) = await BindingTestHost.LoadPinnedInputsAsync();
        var pinned = document.Operations.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);

        var ids = await LongPollIdsAsync();

        await Assert.That(ids).IsNotEmpty();
        await Assert.That(ids.Where(id => !pinned.Contains(id))).IsEmpty();
    }

    private static async Task<string[]> LongPollIdsAsync()
    {
        var fileSystem = new RealFileSystem();
        var path = fileSystem.Path.Combine(AppContext.BaseDirectory, "Fixtures", "SdkSource", TablePath);
        var text = await fileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
        var root = await CSharpSyntaxTree.ParseText(text, path: path).GetRootAsync();
        var field = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(static variable => variable.Identifier.ValueText == TableField);

        // Every entry is read as a literal; one written any other way (a constant, an
        // interpolation) would escape the pinned-id check, so it fails here instead.
        var entries = field.DescendantNodes().OfType<CollectionExpressionSyntax>().Single().Elements;
        var unread = entries.Where(static entry => entry is not ExpressionElementSyntax
        {
            Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression },
        }).Select(static entry => entry.ToString()).ToArray();
        if (unread.Length > 0)
        {
            throw new InvalidOperationException($"The long-poll table holds entries that are not string literals: {string.Join(", ", unread)}");
        }

        return [.. entries.Cast<ExpressionElementSyntax>().Select(static entry => ((LiteralExpressionSyntax)entry.Expression).Token.ValueText)];
    }
}
