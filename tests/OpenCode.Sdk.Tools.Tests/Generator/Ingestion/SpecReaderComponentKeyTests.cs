using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

public sealed class SpecReaderComponentKeyTests
{
    private const string LibraryRuleName = nameof(OpenApiComponentsRules.KeyMustBeRegularExpression);

    [Test]
    public async Task SpecReader_Should_Refuse_An_Invalid_Component_Key_With_The_Library_Error()
    {
        var context = ComponentKeyScenario([ComponentKeyData.ValidKey, ComponentKeyData.InvalidKey]).Build();
        var libraryErrors = await ReadErrorsAsync(context, new OpenApiReaderSettings());
        var reader = new SpecReader(context.FileSystem);

        var ex = await Assert
            .That(async () => _ = await reader.LoadAsync(context.SpecPath, new IngestionErrorCollector(), CancellationToken.None))
            .Throws<IngestionException>();

        // Diagnostic membership and multiplicity matter, not report enumeration order.
        await Assert.That(libraryErrors.Select(static error => error.RuleName ?? "none")).IsEquivalentTo([LibraryRuleName, LibraryRuleName], CollectionOrdering.Any);
        await Assert
            .That(ex!.Errors.Select(static error => $"{error.Location}: {error.Problem}"))
            .IsEquivalentTo(libraryErrors.Select(static error => $"{error.Pointer}: {error.Message}"), CollectionOrdering.Matching);
    }

    private static SpecScenario ComponentKeyScenario(IReadOnlyList<string> keys) =>
        SpecScenario.Define(spec =>
        {
            foreach (var key in keys)
            {
                spec.WithEmptySchema(key).WithResponseComponent(key);
            }
        });

    private static async Task<IReadOnlyList<ReaderError>> ReadErrorsAsync(ScenarioContext context, OpenApiReaderSettings settings)
    {
        var stream = context.FileSystem.File.OpenRead(context.SpecPath);
        await using (stream.ConfigureAwait(false))
        {
            var result = await OpenApiDocument.LoadAsync(stream, "json", settings, CancellationToken.None).ConfigureAwait(false);
            var diagnostic = result.Diagnostic ?? throw new InvalidOperationException("The OpenAPI reader returned no diagnostic information.");
            return
            [
                .. diagnostic.Errors.Select(static error =>
                    new ReaderError((error as OpenApiValidatorError)?.RuleName, error.Pointer, error.Message)),
            ];
        }
    }

    private sealed record ReaderError(string? RuleName, string? Pointer, string Message);
}
