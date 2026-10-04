using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

public sealed class ComponentKeyRuleTests
{
    private const string LibraryRuleName = nameof(OpenApiComponentsRules.KeyMustBeRegularExpression);

    /// <summary>
    /// The library's own pattern comes from its error message, read off one invalid key, so the
    /// library's timed regex runs a handful of times here rather than once per key; every key is
    /// then decided by both patterns in memory, without a timeout.
    /// </summary>
    [Test]
    public async Task KeyRegex_Should_Decide_Every_Short_Component_Key_As_The_Library_Pattern_Does()
    {
        var context = ComponentKeyScenario([ComponentKeyData.InvalidKey]).Build();
        var libraryError = (await ReadErrorsAsync(context, new OpenApiReaderSettings()))[0];
        var libraryPattern = PatternFrom(libraryError.Message);
        var library = new Regex(libraryPattern, RegexOptions.None, Regex.InfiniteMatchTimeout);

        var disagreements = ComponentKeyData.EquivalenceKeys
            .Where(key => ComponentKeyRule.KeyRegex().IsMatch(key) != library.IsMatch(key))
            .ToList();

        await Assert.That(ComponentKeyRule.KeyRegex().ToString()).IsEqualTo(libraryPattern);
        await Assert.That(disagreements).IsEmpty();
        await Assert.That(ComponentKeyData.EquivalenceKeys.Any(library.IsMatch)).IsTrue();
        await Assert.That(ComponentKeyData.EquivalenceKeys.Any(key => !library.IsMatch(key))).IsTrue();
    }

    [Test]
    public async Task CreateReaderRuleSet_Should_Report_The_Library_Errors_For_Invalid_Keys_In_Every_Walked_Map()
    {
        var context = ComponentKeyScenario([ComponentKeyData.ValidKey, ComponentKeyData.InvalidKey, "a\r\n"]).Build();

        var libraryErrors = await ReadErrorsAsync(context, new OpenApiReaderSettings());
        var sdkErrors = await ReadErrorsAsync(context, new OpenApiReaderSettings { RuleSet = ComponentKeyRule.CreateReaderRuleSet() });

        await Assert.That(libraryErrors.Count).IsEqualTo(4);
        await Assert.That(sdkErrors).IsEquivalentTo(libraryErrors, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CreateReaderSettings_Should_Install_The_Components_Key_Rule_In_Place_Of_The_Library_Rule()
    {
        var installed = SpecReader.CreateReaderSettings().RuleSet.FindRules(typeof(OpenApiComponents));

        await Assert.That(installed.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(installed[0], ComponentKeyRule.Rule)).IsTrue();
    }

    /// <summary>
    /// The library builds its cached default set lazily and its rule properties return a new
    /// instance per read, so two concurrent first reads can hold different instances: the check
    /// compares names, and finds the replacement by its own single instance.
    /// </summary>
    [Test]
    public async Task CreateReaderRuleSet_Should_Keep_Every_Default_Rule_And_Replace_Only_The_Components_Key_Rule()
    {
        var defaults = ValidationRuleSet.GetDefaultRuleSet().Rules;

        var replaced = ComponentKeyRule.CreateReaderRuleSet();

        await Assert
            .That(replaced.Rules.Select(static rule => rule.Name).Order(StringComparer.Ordinal))
            .IsEquivalentTo(defaults.Select(static rule => rule.Name).Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        var componentRules = replaced.FindRules(typeof(OpenApiComponents));
        await Assert.That(componentRules.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(componentRules[0], ComponentKeyRule.Rule)).IsTrue();
    }

    [Test]
    public async Task SpecReader_Should_Refuse_An_Invalid_Component_Key_With_The_Library_Error()
    {
        var context = ComponentKeyScenario([ComponentKeyData.ValidKey, ComponentKeyData.InvalidKey]).Build();
        var libraryErrors = await ReadErrorsAsync(context, new OpenApiReaderSettings());
        var reader = new SpecReader(context.FileSystem);

        var ex = await Assert
            .That(async () => _ = await reader.LoadAsync(context.SpecPath, new IngestionErrorCollector(), CancellationToken.None))
            .Throws<IngestionException>();

        await Assert.That(libraryErrors.Select(static error => error.RuleName ?? "none")).IsEquivalentTo([LibraryRuleName, LibraryRuleName]);
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

    private static string PatternFrom(string message)
    {
        const string marker = "MUST match the regular expression '";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        const string end = "'.";
        return message.EndsWith(end, StringComparison.Ordinal)
            ? message[start..^end.Length]
            : throw new InvalidOperationException($"Unexpected components-key error text: {message}");
    }

    private sealed record ReaderError(string? RuleName, string? Pointer, string Message);
}
