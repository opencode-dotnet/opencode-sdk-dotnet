using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Generator.Binding.Support.OwnershipPlanData;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// One test per rule of the declared model ownership: a body request lives with the family that
/// sends it, a union's kit follows its union, a hoisted interface follows its concept, an owner row
/// places what no prefix covers, otherwise the longest family prefix at a word boundary decides;
/// Sessions models then split into the curated areas their names match. Every refusal is a curation
/// error naming its row, prefix alias, group or model.
/// </summary>
public sealed class ModelOwnershipPolicyTests
{
    public static IEnumerable<object[]> OwnerRefusals() =>
    [
        ["unknown", new BindingError(BindingErrorCategory.Curation, "ProviderAuth", "model owner 'Unknown' is neither an admitted family nor 'Common'")],
        ["overlap", new BindingError(BindingErrorCategory.Curation, "ProviderAuthMethod", "model owner rows 'Provider', 'ProviderAuth' all match the model")],
        ["stale", new BindingError(BindingErrorCategory.Curation, "Absent", "model owner row places no generated model")],
        ["body", new BindingError(BindingErrorCategory.Curation, "SessionLoginRequest", "a body request sent by both 'Credentials' and 'Sessions' has no single family root")],
    ];

    [Test]
    [MethodDataSource(nameof(OwnerRefusals))]
    public async Task Place_Should_Refuse_Invalid_Owner_Rows(string fault, BindingError expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ModelOwnerCuration[] rows = fault switch
        {
            "unknown" => [Owner("ProviderAuth", "Unknown")],
            "overlap" => [Owner("Provider", "Credentials"), Owner("ProviderAuth", "Common")],
            "stale" => [Owner("Absent", "Common")],
            _ => [],
        };
        var errors = new BindingErrorCollector();
        _ = Place(errors, OwnershipPlanData.Models("ProviderAuthMethod", "SessionLoginRequest"), Ownership(owners: rows),
            bodyOperations: fault == "body" ? [BodyOperation("Credentials", "SessionLoginRequest"), BodyOperation("Sessions", "SessionLoginRequest")] : []);
        await Assert.That(Assert.Throws<BindingException>(errors.ThrowIfAny).Errors
            .Single(error => error.Category == expected.Category && error.Subject == expected.Subject))
            .IsEqualTo(expected);
    }

    [Test]
    [Arguments("family", "sessionAreas", "session areas need the 'Sessions' family")]
    [Arguments("reserved", "Common", "session area name is empty, reserved, not an identifier, or duplicated")]
    [Arguments("identifier", "bad-name", "session area name is empty, reserved, not an identifier, or duplicated")]
    [Arguments("empty", "Lifecycle", "session area patterns are empty or repeat another pattern")]
    [Arguments("blank", "Lifecycle", "session area patterns are empty or repeat another pattern")]
    [Arguments("repeat", "Lifecycle", "session area patterns are empty or repeat another pattern")]
    [Arguments("cross-repeat", "Other", "session area patterns are empty or repeat another pattern")]
    [Arguments("overlap", "SessionCreated", "session area patterns 'Session', 'SessionCreated' place the model in more than one area")]
    [Arguments("stale", "Lifecycle/Absent", "session area pattern places no generated model")]
    public async Task Place_Should_Refuse_Invalid_Area_Rows(string fault, string subject, string problem)
    {
        var areas = fault switch
        {
            "reserved" => new[] { Area("Common", "Session") },
            "identifier" => [Area("bad-name", "Session")],
            "empty" => [Area("Lifecycle")],
            "blank" => [Area("Lifecycle", " ")],
            "repeat" => [Area("Lifecycle", "Session", "Session")],
            "cross-repeat" => [Area("Lifecycle", "Session"), Area("Other", "Session")],
            "overlap" => [Area("Lifecycle", "Session"), Area("Other", "SessionCreated")],
            "stale" => [Area("Lifecycle", "Absent")],
            _ => [Area("Lifecycle", "Session")],
        };
        var errors = new BindingErrorCollector();
        var groups = fault == "family" ? new Dictionary<string, GroupCuration>(StringComparer.Ordinal) { ["credential"] = FamilyGroup("Credentials") } : Families();
        _ = Place(errors, OwnershipPlanData.Models("SessionCreated"), Ownership(groups, areas: areas));
        await Assert.That(Assert.Throws<BindingException>(errors.ThrowIfAny).Errors
            .Single(error => error.Category == BindingErrorCategory.Curation && error.Subject == subject))
            .IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, problem));
    }

    private const string Sessions = "Sessions";
    private const string Credentials = "Credentials";
    private const string McpServers = "McpServers";
    private const string LanguageModels = "LanguageModels";
    private const string Examples = "Examples";
    private const string Lifecycle = "Lifecycle";
    private const string SessionCreated = "SessionCreated";
    private const string ProviderAuth = "ProviderAuth";
    private const string ProviderAuthMethod = "ProviderAuthMethod";
    private const string SessionLoginRequest = "SessionLoginRequest";
    private const string SessionInfo = "SessionInfo";
    private const string ModelsDevRefreshed = "ModelsDevRefreshed";
    private const string OtherFamily = "Vcs";
    private const string BlankReason = " ";
    private const string BlankAlias = " ";
    private const string ModelInfo = "ModelInfo";
    private const string ModelAlias = "Model";
    private const string SessionAlias = "Session";
    private const string HoistedExampleTime = "IExampleTime";

    public static IEnumerable<object?[]> UnsafeOwnershipRows() =>
    [
        ["ModelOwners", null, IndexedContractError("modelOwners", "curation is null")],
        ["ModelOwners", "Match", IndexedContractError("modelOwners", "curation names no match")],
        ["SessionAreas", null, IndexedContractError("sessionAreas", "curation is null")],
        ["SessionAreas", "Area", IndexedContractError("sessionAreas", "curation names no area")],
    ];

    [Test]
    [MethodDataSource(nameof(UnsafeOwnershipRows))]
    public async Task Bind_Should_Refuse_Unsafe_Ownership_Rows(string property, string? blankField, BindingError expected)
    {
        var exception = await BindUnsafeContractRowAsync(property, blankField);

        await Assert.That(exception.Errors.Single()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Common")]
    [Arguments("not-an-identifier")]
    public async Task Create_Should_Check_The_Reason_Before_Rejecting_An_Area(string area)
    {
        var errors = new BindingErrorCollector();
        _ = SessionAreaRows.Create([new SessionAreaCuration { Area = area, Matches = ["Session"], Reason = " " }],
            new HashSet<string>(StringComparer.Ordinal) { Sessions }, errors);
        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);

        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Curation, area, "session area curation must declare a reason"),
            new BindingError(BindingErrorCategory.Curation, area, "session area name is empty, reserved, not an identifier, or duplicated"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Place_Should_Place_A_Model_By_A_Group_Prefix_Alias()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models(ModelsDevRefreshed), Ownership(FamiliesWithModelAlias()));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(ModelsDevRefreshed)).IsEqualTo(TypeHome.Family(LanguageModels));
    }

    /// <summary>A row cannot restate the placement a family prefix already supplies.</summary>
    [Test]
    [Arguments(ModelsDevRefreshed, "ModelsDev", LanguageModels, true, "model owner row is redundant: family prefix already places model 'ModelsDevRefreshed' in 'LanguageModels'")]
    [Arguments(SessionInfo, "Session", Sessions, false, "model owner row is redundant: family prefix already places model 'SessionInfo' in 'Sessions'")]
    public async Task Place_Should_Refuse_An_Owner_Row_That_Confirms_A_Family_Prefix(string model, string match, string family, bool prefixAlias, string problem)
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models(model),
            Ownership(prefixAlias ? FamiliesWithModelAlias() : Families(), owners: [Owner(match, family)]));

        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);
        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, match,
            problem));
        await Assert.That(placement.HomeOf(model)).IsEqualTo(TypeHome.Family(family));
    }

    /// <summary>An owner row that sends the alias's only model elsewhere leaves the alias placing nothing.</summary>
    [Test]
    public async Task Place_Should_Refuse_A_Prefix_Alias_When_An_Owner_Row_Places_Its_Model_In_Another_Family()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models(ModelsDevRefreshed),
            Ownership(FamiliesWithModelAlias(), owners: [Owner("ModelsDev", OtherFamily)]));

        await Assert.That(placement.HomeOf(ModelsDevRefreshed)).IsEqualTo(TypeHome.Family(OtherFamily));
        await AssertRefusedAsync(errors, ModelsAlias, $"the prefix alias of '{LanguageModels}' places no generated model");
    }

    [Test]
    public async Task Place_Should_Refuse_A_Prefix_Alias_That_Places_No_Model()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, [], Ownership(FamiliesWithModelAlias()));

        await AssertRefusedAsync(errors, ModelsAlias, $"the prefix alias of '{LanguageModels}' places no generated model");
    }

    [Test]
    [Arguments(BlankAlias)]
    [Arguments("Models-Dev")]
    [Arguments("class")]
    public async Task Place_Should_Refuse_A_Prefix_Alias_Naming_Its_Group_Key_When_The_Alias_Is_Blank_Or_Not_An_Identifier(string alias)
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, [], Ownership(FamiliesWithModelAlias(alias)));

        await AssertRefusedAsync(errors, ModelGroupKey, "is blank or not an identifier");
    }

    /// <summary>The <c>Session</c> alias collides with the singular of the <c>Sessions</c> family.</summary>
    [Test]
    public async Task Place_Should_Refuse_A_Prefix_Alias_That_Another_Family_Already_Claims()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(SessionInfo), Ownership(FamiliesWithModelAlias(SessionAlias)));

        await AssertRefusedAsync(errors, SessionAlias, $"the name prefix places models in both '{LanguageModels}' and '{Sessions}'");
    }

    /// <summary>The <c>Model</c> alias repeats the Pascal-cased group key of its own family.</summary>
    [Test]
    public async Task Place_Should_Refuse_A_Prefix_Alias_That_Repeats_A_Prefix_Of_Its_Own_Family()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(ModelInfo), Ownership(FamiliesWithModelAlias(ModelAlias)));

        await AssertRefusedAsync(errors, ModelAlias, $"the prefix alias repeats a prefix family '{LanguageModels}' already has");
    }

    /// <summary>
    /// The shorter <c>Persistent</c> prefix, curated first, also matches <c>PersistentPtyInfo</c> at a
    /// word boundary; the longer <c>PersistentPty</c> decides.
    /// </summary>
    [Test]
    public async Task Place_Should_Prefer_The_Longest_Matching_Family_Prefix()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("PersistentPtyInfo", "PersistentStore"), Ownership(PersistentFamilies()));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf("PersistentPtyInfo")).IsEqualTo(TypeHome.Family("PersistentPtys"));
        await Assert.That(placement.HomeOf("PersistentStore")).IsEqualTo(TypeHome.Family("Persistent"));
    }

    [Test]
    public async Task Place_Should_Not_Match_A_Family_Prefix_When_The_Next_Character_Is_Lower_Case()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("FormInfo", "FormatInfo"));

        await Assert.That(placement.HomeOf("FormInfo")).IsEqualTo(TypeHome.Family("Forms"));
        // Only membership and cardinality matter; this expected collection has one item.
        await Assert.That(placement.Unplaced).IsEquivalentTo(["FormatInfo"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Place_Should_Match_A_Family_Prefix_Ignoring_Its_Case()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("WebSearchResult"));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf("WebSearchResult")).IsEqualTo(TypeHome.Family("Websearch"));
    }

    [Test]
    [Arguments("IExampleEvent")]
    [Arguments("UnknownExampleEvent")]
    public async Task Place_Should_Place_A_Union_Interface_By_Its_Concept_Name_With_Its_Unknown_Carrier(string typeName)
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, [], unions: [Union("ExampleEvent")]);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(typeName)).IsEqualTo(TypeHome.Family(Examples));
    }

    /// <summary><c>IExampleTime</c> matches no prefix itself; its concept <c>ExampleTime</c> names the family.</summary>
    [Test]
    public async Task Place_Should_Place_A_Hoisted_Interface_In_The_Family_Its_Concept_Prefix_Names()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, [], hoistedInterfaces: [HoistedInterface(HoistedExampleTime)]);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(HoistedExampleTime)).IsEqualTo(TypeHome.Family(Examples));
    }

    [Test]
    [Arguments("OutputValue")]
    [Arguments("OutputValueKind")]
    public async Task Place_Should_Place_A_Structural_Union_Kind_With_Its_Structural_Union(string typeName)
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, [StructuralUnion("OutputValue", "OutputValueKind")],
            Ownership(owners: [Owner("OutputValue", Credentials)]));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(typeName)).IsEqualTo(TypeHome.Family(Credentials));
    }

    [Test]
    public async Task Place_Should_Place_A_Body_Request_With_The_Family_That_Sends_It_When_Its_Name_Has_Another_Family_Prefix()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models(SessionLoginRequest), bodyOperations: [BodyOperation(Credentials, SessionLoginRequest)]);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(SessionLoginRequest)).IsEqualTo(TypeHome.Family(Credentials));
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert.That(placement.BodyRequests).IsEquivalentTo([SessionLoginRequest], CollectionOrdering.Any);
    }

    [Test]
    public async Task Place_Should_Place_A_Binary_Body_By_Its_Name_And_Not_List_It_As_A_Body_Request()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("SessionUpload"), bodyOperations: [BodyOperation(Credentials, "SessionUpload", isBinary: true)]);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf("SessionUpload")).IsEqualTo(TypeHome.Family(Sessions));
        await Assert.That(placement.BodyRequests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Place_Should_Refuse_A_Body_Request_When_Two_Families_Send_It()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(SessionLoginRequest),
            bodyOperations: [BodyOperation(Credentials, SessionLoginRequest), BodyOperation(McpServers, SessionLoginRequest)]);

        await AssertRefusedAsync(errors, SessionLoginRequest, "has no single family root");
    }

    [Test]
    [Arguments(ProviderAuthMethod, ProviderAuth, Credentials)]
    [Arguments("NotFoundError", "NotFoundError", TypeHome.CommonSlice)]
    public async Task Place_Should_Place_An_Unprefixed_Model_By_Its_Owner_Row(string model, string match, string owner)
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models(model), Ownership(owners: [Owner(match, owner)]));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(model)).IsEqualTo(TypeHome.Family(owner));
    }

    [Test]
    public async Task Place_Should_Prefer_An_Owner_Row_Over_A_Family_Prefix()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("SessionError"), Ownership(owners: [Owner("SessionError", TypeHome.CommonSlice)]));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf("SessionError")).IsEqualTo(TypeHome.Family(TypeHome.CommonSlice));
    }

    [Test]
    public async Task Place_Should_Refuse_A_Model_When_Two_Owner_Rows_Match_It()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(ProviderAuthMethod), Ownership(owners: [Owner("Provider", Credentials), Owner(ProviderAuth, McpServers)]));

        await AssertRefusedAsync(errors, ProviderAuthMethod, "all match the model");
    }

    [Test]
    public async Task Place_Should_Refuse_An_Owner_Row_That_Places_No_Model()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, [], Ownership(owners: [Owner(ProviderAuth, Credentials)]));

        await AssertRefusedAsync(errors, ProviderAuth, "places no generated model");
    }

    [Test]
    public async Task Place_Should_Refuse_An_Owner_Row_Whose_Owner_Is_Not_An_Admitted_Family()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(ProviderAuthMethod), Ownership(owners: [Owner(ProviderAuth, "Providers")]));

        await AssertRefusedAsync(errors, ProviderAuth, "is neither an admitted family nor 'Common'");
    }

    [Test]
    public async Task Place_Should_Refuse_An_Owner_Row_Without_A_Reason()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(ProviderAuthMethod), Ownership(owners: [Owner(ProviderAuth, Credentials, reason: BlankReason)]));

        await AssertRefusedAsync(errors, ProviderAuth, "must declare a reason");
    }

    [Test]
    public async Task Place_Should_List_An_Unprefixed_Model_As_Unplaced_When_No_Owner_Row_Places_It()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("OutputFormat"));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.Homes).DoesNotContainKey("OutputFormat");
        // Only membership and cardinality matter; this expected collection has one item.
        await Assert.That(placement.Unplaced).IsEquivalentTo(["OutputFormat"], CollectionOrdering.Any);
    }

    [Test]
    [Arguments(SessionCreated)]
    [Arguments("SessionCreatedData")]
    [Arguments("SessionCreatedDurable")]
    public async Task Place_Should_Give_A_Sessions_Model_And_Its_Companions_The_Area_Its_Name_Matches(string model)
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models(SessionCreated, "SessionCreatedData", "SessionCreatedDurable"),
            Ownership(areas: [Area(Lifecycle, SessionCreated)]));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(model)).IsEqualTo(SessionsArea(Lifecycle));
    }

    [Test]
    public async Task Place_Should_Leave_The_Area_Empty_When_A_Sessions_Model_Matches_No_Area_Pattern()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models(SessionCreated, SessionInfo), Ownership(areas: [Area(Lifecycle, SessionCreated)]));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf(SessionInfo)).IsEqualTo(TypeHome.Family(Sessions));
    }

    [Test]
    public async Task Place_Should_Not_Match_An_Area_Pattern_When_The_Next_Character_Is_Lower_Case()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("SessionForkBoundary", "SessionForked"), Ownership(areas: [Area("Fork", "SessionFork")]));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf("SessionForkBoundary")).IsEqualTo(SessionsArea("Fork"));
        await Assert.That(placement.HomeOf("SessionForked")).IsEqualTo(TypeHome.Family(Sessions));
    }

    [Test]
    public async Task Place_Should_Not_Give_An_Area_When_Another_Family_Owns_The_Model()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("ToolPart", "ToolDefinition"),
            Ownership(owners: [Owner("ToolPart", Sessions), Owner("ToolDefinition", McpServers)], areas: [Area("Turn", "Tool")]));

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf("ToolPart")).IsEqualTo(SessionsArea("Turn"));
        await Assert.That(placement.HomeOf("ToolDefinition")).IsEqualTo(TypeHome.Family(McpServers));
    }

    [Test]
    public async Task Place_Should_Not_Give_An_Area_To_A_Body_Request()
    {
        var errors = new BindingErrorCollector();

        var placement = Place(errors, OwnershipPlanData.Models("SessionCreateRequest", "SessionCreateOptions"), Ownership(areas: [Area("Create", "SessionCreate")]),
            bodyOperations: [BodyOperation(Sessions, "SessionCreateRequest")]);

        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(placement.HomeOf("SessionCreateRequest")).IsEqualTo(TypeHome.Family(Sessions));
        await Assert.That(placement.HomeOf("SessionCreateOptions")).IsEqualTo(SessionsArea("Create"));
    }

    [Test]
    public async Task Place_Should_Refuse_A_Model_When_Two_Areas_Match_It()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(SessionCreated), Ownership(areas: [Area(Lifecycle, SessionCreated), Area("Events", "Session")]));

        await AssertRefusedAsync(errors, SessionCreated, "place the model in more than one area");
    }

    [Test]
    public async Task Place_Should_Refuse_An_Area_Pattern_That_Places_No_Model()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(SessionCreated), Ownership(areas: [Area(Lifecycle, SessionCreated, "SessionMoved")]));

        await AssertRefusedAsync(errors, $"{Lifecycle}/SessionMoved", "session area pattern places no generated model");
    }

    [Test]
    [Arguments("Common")]
    [Arguments("Internal")]
    [Arguments("Hosting")]
    [Arguments("Properties")]
    [Arguments("Models")]
    [Arguments("models")]
    public async Task Place_Should_Refuse_A_Reserved_Area_Name(string area)
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(SessionCreated), Ownership(areas: [Area(area, SessionCreated)]));

        await AssertRefusedAsync(errors, area, "reserved");
    }

    [Test]
    public async Task Place_Should_Refuse_An_Area_Row_Without_A_Reason()
    {
        var errors = new BindingErrorCollector();

        _ = Place(errors, OwnershipPlanData.Models(SessionCreated), Ownership(areas: [Area(Lifecycle, SessionCreated) with { Reason = BlankReason }]));

        await AssertRefusedAsync(errors, Lifecycle, "session area curation must declare a reason");
    }

    [Test]
    public async Task Place_Should_Refuse_Area_Rows_When_No_Sessions_Family_Is_Admitted()
    {
        var errors = new BindingErrorCollector();
        var groups = Families();
        _ = groups.Remove("session");

        _ = Place(errors, OwnershipPlanData.Models(SessionCreated), Ownership(groups, areas: [Area(Lifecycle, SessionCreated)]));

        await AssertRefusedAsync(errors, "sessionAreas", "session areas need the 'Sessions' family");
    }

    private static TypeHome SessionsArea(string area) => new() { Slice = Sessions, Area = area };
    private static async Task AssertRefusedAsync(BindingErrorCollector errors, string subject, string problem)
    {
        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);

        await Assert.That(exception.Errors).Contains(error => error.Category == BindingErrorCategory.Curation
                                                        && string.Equals(error.Subject, subject, StringComparison.Ordinal)
                                                        && error.Problem.Contains(problem, StringComparison.Ordinal));
    }
}
