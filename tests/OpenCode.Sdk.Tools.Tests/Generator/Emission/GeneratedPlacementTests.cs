using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// Pins the one table that turns a generated file's kind and owner into its path. Every owned kind
/// refuses a missing owner even where its folder does not depend on the owner yet, so the owner is
/// known for every file before the folders move to their slices. Kinds travel as names because the
/// enum is internal to the generator and a public test method cannot take it.
/// </summary>
public sealed class GeneratedPlacementTests
{
    private const string SessionsSlice = "Sessions";
    private const string MessagesArea = "Messages";
    private const string SessionInfo = "SessionInfo";
    private const string SessionMessage = "SessionMessage";
    private const string SessionForkRequest = "SessionForkRequest";
    private const string UnplacedName = "SessionStray";

    [Test]
    [Arguments("Model", SessionsSlice, null, SessionInfo, "Sessions/Models/SessionInfo.cs")]
    [Arguments("Model", SessionsSlice, MessagesArea, SessionMessage, "Sessions/Models/Messages/SessionMessage.cs")]
    [Arguments("Model", TypeHome.CommonSlice, null, "NotFoundError", "Common/Errors/NotFoundError.cs")]
    [Arguments("BodyRequest", SessionsSlice, null, SessionForkRequest, "Sessions/SessionForkRequest.cs")]
    [Arguments("OperationRecord", SessionsSlice, null, "SessionListRequest", "Sessions/SessionListRequest.cs")]
    [Arguments("Client", SessionsSlice, null, "SessionsClient", "Sessions/SessionsClient.cs")]
    [Arguments("RawClient", "Ptys", null, "PtysRawClient", "Ptys/Internal/PtysRawClient.cs")]
    [Arguments("RootClient", null, null, "OpenCodeClient", "OpenCodeClient.cs")]
    [Arguments("Routes", SessionsSlice, null, "OpenCodeRoutes", "Sessions/Internal/OpenCodeRoutes.Sessions.cs")]
    [Arguments("ModelConverter", SessionsSlice, MessagesArea, "PartJsonConverter", "Sessions/Internal/PartJsonConverter.cs")]
    [Arguments("ResponseAdapter", "Agents", null, "AgentListResponseAdapter", "Agents/Internal/AgentListResponseAdapter.cs")]
    [Arguments("EnvelopeDto", SessionsSlice, null, "SessionGetEnvelopeDto", "Sessions/Internal/SessionGetEnvelopeDto.cs")]
    [Arguments("StreamAdapter", "Events", null, "EventSubscribeStreamAdapter", "Events/Internal/EventSubscribeStreamAdapter.cs")]
    [Arguments("SharedSerialization", null, null, "OpenCodeJsonContext", "Internal/Serialization/OpenCodeJsonContext.cs")]
    public async Task PathOf_Should_Place_Each_Kind_In_Its_Folder(string kindName, string? slice, string? area, string typeName,
        string expected)
    {
        var kind = Enum.Parse<GeneratedKind>(kindName);

        await Assert.That(GeneratedPlacement.PathOf(kind, Owner(slice, area), typeName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Model")]
    [Arguments("BodyRequest")]
    [Arguments("OperationRecord")]
    [Arguments("Client")]
    [Arguments("RawClient")]
    [Arguments("Routes")]
    [Arguments("ModelConverter")]
    [Arguments("ResponseAdapter")]
    [Arguments("EnvelopeDto")]
    [Arguments("StreamAdapter")]
    public async Task PathOf_Should_Refuse_A_Missing_Owner_When_The_Kind_Is_Owned(string kindName)
    {
        var kind = Enum.Parse<GeneratedKind>(kindName);

        var exception = Assert.Throws<InvalidOperationException>(() => _ = GeneratedPlacement.PathOf(kind, null, SessionInfo));

        await Assert.That(exception.Message).IsEqualTo($"A generated {kind} needs an owner.");
    }

    /// <summary>The kind is pinned beside the path, so the body-request choice still shows once the two kinds' folders part.</summary>
    [Test]
    [Arguments(SessionForkRequest, "BodyRequest", "Sessions/SessionForkRequest.cs")]
    [Arguments(SessionInfo, "Model", "Sessions/Models/SessionInfo.cs")]
    [Arguments(SessionMessage, "Model", "Sessions/Models/Messages/SessionMessage.cs")]
    public async Task ModelPathOf_Should_Place_A_Body_Request_As_A_Body_Request_And_Every_Other_Name_As_A_Model(string typeName,
        string kindName, string expected)
    {
        var kind = Enum.Parse<GeneratedKind>(kindName);

        var placement = Placement();

        var path = GeneratedPlacement.ModelPathOf(placement, typeName);

        await Assert.That(path).IsEqualTo(expected);
        await Assert.That(path).IsEqualTo(GeneratedPlacement.PathOf(kind, placement.HomeOf(typeName), typeName));
    }

    [Test]
    public async Task ModelPathOf_Should_Refuse_A_Name_Without_A_Placement()
    {
        var exception = Assert.Throws<InvalidOperationException>(static () => _ = GeneratedPlacement.ModelPathOf(Placement(), UnplacedName));

        await Assert.That(exception.Message).IsEqualTo($"Generated type '{UnplacedName}' has no placement.");
    }

    /// <summary>The converter's own name has no placement, so a path at all proves the union's home placed it.</summary>
    [Test]
    [Arguments(SessionMessage, "SessionMessageJsonConverter", "Sessions/Internal/SessionMessageJsonConverter.cs")]
    public async Task ConverterPathOf_Should_Place_A_Converter_By_The_Home_Of_Its_Union(string unionName, string converterName,
        string expected)
    {
        var placement = Placement();

        var path = GeneratedPlacement.ConverterPathOf(placement, unionName, converterName);

        await Assert.That(path).IsEqualTo(expected);
        await Assert.That(path)
            .IsEqualTo(GeneratedPlacement.PathOf(GeneratedKind.ModelConverter, placement.HomeOf(unionName), converterName));
    }

    [Test]
    public async Task ConverterPathOf_Should_Refuse_A_Union_Without_A_Placement()
    {
        var exception = Assert.Throws<InvalidOperationException>(static () =>
            _ = GeneratedPlacement.ConverterPathOf(Placement(), UnplacedName, $"{UnplacedName}JsonConverter"));

        await Assert.That(exception.Message).IsEqualTo($"Generated type '{UnplacedName}' has no placement.");
    }

    private static TypeHome? Owner(string? slice, string? area) => slice is null ? null : new TypeHome { Slice = slice, Area = area };

    private static ModelPlacement Placement() => new()
    {
        Homes = new Dictionary<string, TypeHome>(StringComparer.Ordinal)
        {
            [SessionInfo] = TypeHome.Family(SessionsSlice),
            [SessionMessage] = new() { Slice = SessionsSlice, Area = MessagesArea },
            [SessionForkRequest] = TypeHome.Family(SessionsSlice),
        },
        BodyRequests = [SessionForkRequest],
    };
}
