using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Generator.Binding.Support.OwnershipPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// The prefixes that place a model in a family: the family name, its singular, its Pascal-cased
/// group key and its prefix aliases. The longest prefix that starts a concept at a word boundary
/// names the family, and only an alias that named no family is reported dead.
/// </summary>
public sealed class FamilyPrefixTableTests
{
    [Test]
    [Arguments("collision", "Session", "the name prefix places models in both 'LanguageModels' and 'Sessions'", "the name prefix places models in both 'LanguageModels' and 'Sessions'")]
    [Arguments("repeat", "Model", "the prefix alias repeats a prefix family 'LanguageModels' already has")]
    [Arguments("stale", "Models", "the prefix alias of 'LanguageModels' places no generated model")]
    [Arguments("blank", "model", "the prefix alias ' ' is blank or not an identifier")]
    [Arguments("invalid", "model", "the prefix alias 'bad-name' is blank or not an identifier")]
    public async Task Create_Should_Refuse_Invalid_Prefix_Aliases(string fault, string subject, string problem, string? secondProblem = null)
    {
        var alias = fault switch { "collision" => "Session", "repeat" => "Model", "blank" => " ", "invalid" => "bad-name", _ => "Models" };
        var errors = new BindingErrorCollector();
        var table = PrefixTable(errors, FamiliesWithModelAlias(alias));
        if (fault == "stale")
        {
            table.ReportDeadAliases(errors);
        }
        BindingError[] expected = secondProblem is null ? [new(BindingErrorCategory.Curation, subject, problem)]
            : [new(BindingErrorCategory.Curation, subject, problem), new(BindingErrorCategory.Curation, subject, secondProblem)];
        await Assert.That(Assert.Throws<BindingException>(errors.ThrowIfAny).Errors
            .Where(error => error.Category == BindingErrorCategory.Curation && error.Subject == subject))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private const string Sessions = "Sessions";
    private const string McpServers = "McpServers";
    private const string LanguageModels = "LanguageModels";
    private const string Vcs = "Vcs";
    private const string ModelsDevRefreshed = "ModelsDevRefreshed";
    private const string OtherSlice = "Events";
    private const string ModelInfo = "ModelInfo";

    [Test]
    [Arguments("PersistentPtyInfo", "PersistentPtys")]
    [Arguments("PersistentStore", "Persistent")]
    public async Task FamilyOf_Should_Return_The_Family_Of_The_Longest_Matching_Prefix(string concept, string family)
    {
        var errors = new BindingErrorCollector();

        var table = PrefixTable(errors, PersistentFamilies());

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(table.FamilyOf(concept)).IsEqualTo(family);
    }

    /// <summary>
    /// A family's prefixes are its name (<c>SessionsCount</c>), its singular (<c>SessionInfo</c>) and its
    /// Pascal-cased group key (<c>McpStatus</c>, where the key <c>mcp</c> names <c>McpServers</c>).
    /// </summary>
    [Test]
    [Arguments("SessionsCount", Sessions)]
    [Arguments("McpServersStatus", McpServers)]
    [Arguments("VcsInfo", Vcs)]
    [Arguments("SessionInfo", Sessions)]
    [Arguments("McpServerConfig", McpServers)]
    [Arguments("LanguageModelInfo", LanguageModels)]
    [Arguments("McpStatus", McpServers)]
    [Arguments(ModelInfo, LanguageModels)]
    public async Task FamilyOf_Should_Return_The_Family_Whose_Prefix_Starts_The_Concept(string concept, string family)
    {
        var table = PrefixTable(new BindingErrorCollector());

        await Assert.That(table.FamilyOf(concept)).IsEqualTo(family);
    }

    /// <summary>The <c>Vcs</c> family is its own group key <c>vcs</c>, so <c>Vc</c> is no prefix of it.</summary>
    [Test]
    public async Task FamilyOf_Should_Return_Null_When_Only_A_Singular_Of_A_Family_Equal_To_Its_Group_Key_Ignoring_Case_Would_Match()
    {
        var table = PrefixTable(new BindingErrorCollector());

        await Assert.That(table.FamilyOf("VcInfo")).IsNull();
    }

    [Test]
    public async Task FamilyOf_Should_Return_The_Family_Whose_Prefix_Alias_Starts_The_Concept()
    {
        var table = PrefixTable(new BindingErrorCollector(), FamiliesWithModelAlias());

        await Assert.That(table.FamilyOf(ModelsDevRefreshed)).IsEqualTo(LanguageModels);
    }

    [Test]
    [Arguments("OutputFormat")]
    [Arguments("FormatInfo")]
    [Arguments(ModelsDevRefreshed)]
    public async Task FamilyOf_Should_Return_Null_When_No_Prefix_Starts_The_Concept(string concept)
    {
        var table = PrefixTable(new BindingErrorCollector());

        await Assert.That(table.FamilyOf(concept)).IsNull();
    }

    /// <summary><c>ModelInfo</c> is named by the group key, so the <c>Models</c> alias placed nothing.</summary>
    [Test]
    public async Task ReportDeadAliases_Should_Report_Only_The_Alias_That_Named_No_Family()
    {
        var errors = new BindingErrorCollector();
        var table = PrefixTable(errors, FamiliesWithModelAlias());
        table.RecordPlacement(ModelInfo, LanguageModels);

        table.ReportDeadAliases(errors);

        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);
        await Assert.That(exception.Errors).Count().IsEqualTo(1);
        await Assert.That(exception.Errors[0].Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(exception.Errors[0].Subject).IsEqualTo(ModelsAlias);
    }

    [Test]
    public async Task ReportDeadAliases_Should_Report_Nothing_When_Every_Alias_Named_A_Family()
    {
        var errors = new BindingErrorCollector();
        var table = PrefixTable(errors, FamiliesWithModelAlias());
        table.RecordPlacement(ModelsDevRefreshed, LanguageModels);

        table.ReportDeadAliases(errors);

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    /// <summary>An alias placed nothing when the model it matches ended up in another slice.</summary>
    [Test]
    public async Task ReportDeadAliases_Should_Report_An_Alias_Whose_Match_Was_Placed_In_Another_Slice()
    {
        var errors = new BindingErrorCollector();
        var table = PrefixTable(errors, FamiliesWithModelAlias());
        table.RecordPlacement(ModelsDevRefreshed, OtherSlice);

        table.ReportDeadAliases(errors);

        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);
        await Assert.That(exception.Errors).Count().IsEqualTo(1);
        await Assert.That(exception.Errors[0].Subject).IsEqualTo(ModelsAlias);
    }
}
