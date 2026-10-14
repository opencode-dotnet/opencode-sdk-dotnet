using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class OperationNamePolicyTests
{
    [Test]
    [Arguments("health.get", "get", "GetHealthAsync")]
    [Arguments("session.message", "get", "GetMessageAsync")]
    [Arguments("session.get", "get", "GetSessionAsync")]
    [Arguments("session.list", "get", "ListSessionsAsync")]
    [Arguments("session.create", "post", "CreateSessionAsync")]
    [Arguments("message.list", "get", "ListMessagesAsync")]
    [Arguments("session.remove", "delete", "RemoveSessionAsync")]
    [Arguments("session.rename", "post", "RenameSessionAsync")]
    [Arguments("session.update", "patch", "UpdateSessionAsync")]
    [Arguments("pty.update", "put", "UpdatePtyAsync")]
    [Arguments("shell.list", "get", "ListShellsAsync")]
    [Arguments("shell.timeout", "patch", "TimeoutShellAsync")]
    [Arguments("shell.output", "get", "GetOutputAsync")]
    [Arguments("session.diff", "get", "GetDiffAsync")]
    public async Task MethodName_Should_Lead_With_The_Grammar_Verb_Or_Read_A_Get(string operationId, string method, string expected)
    {
        await Assert.That(OperationNamePolicy.MethodName(Operation(operationId, method))).IsEqualTo(expected);
    }

    /// <summary>
    /// The HTTP method is never a name source (ADR-0008): an operation whose closing segment is not
    /// a grammar verb and whose method is not GET has no mechanical name and refuses until an
    /// operationNames row names it.
    /// </summary>
    [Test]
    [Arguments("session.prompt", "post", "prompt")]
    [Arguments("session.revert.clear", "delete", "clear")]
    [Arguments("integration.connect.key", "post", "key")]
    [Arguments("experimental.session.instructions.entry.put", "put", "put")]
    public async Task MethodName_Should_Refuse_A_Non_Get_Without_A_Grammar_Verb(string operationId, string method, string closing)
    {
        var operation = Operation(operationId, method);

        await Assert.That(OperationNamePolicy.MethodName(operation)).IsNull();
        await Assert.That(OperationNamePolicy.RowRequiredProblem(operation)).IsEqualTo(
            $"operation '{operationId}' closes with '{closing}', which is not a naming verb, and no operationNames row names it; "
            + "the HTTP method is never a name source (ADR-0008)");
    }

    [Test]
    [Arguments("session.get", "get")]
    [Arguments("session.diff", "get")]
    [Arguments("session.create", "post")]
    public async Task RowRequiredProblem_Should_Be_Null_When_A_Mechanical_Name_Exists(string operationId, string method)
    {
        await Assert.That(OperationNamePolicy.RowRequiredProblem(Operation(operationId, method))).IsNull();
    }

    /// <summary>On a handle client the empty subject is the handle itself, not the family.</summary>
    [Test]
    [Arguments("session.get", "get", "GetAsync")]
    [Arguments("session.remove", "delete", "RemoveAsync")]
    [Arguments("session.update", "patch", "UpdateAsync")]
    [Arguments("session.message.get", "get", "GetMessageAsync")]
    public async Task MethodName_Should_Drop_The_Family_From_An_Empty_Subject_On_A_Handle(string operationId, string method, string expected)
    {
        await Assert.That(OperationNamePolicy.MethodName(Operation(operationId, method), handle: true)).IsEqualTo(expected);
    }

    [Test]
    public async Task MethodName_Should_Keep_A_Mid_Position_Verb_Segment_In_The_Subject()
    {
        await Assert.That(OperationNamePolicy.MethodName(Operation("cache.get.entry")))
            .IsEqualTo("GetGetEntryAsync");
    }

    [Test]
    public async Task MethodName_Should_Return_Null_When_The_Group_Cannot_Be_Pluralized()
    {
        await Assert.That(OperationNamePolicy.MethodName(Operation("boss.list"))).IsNull();
    }

    [Test]
    [Arguments("ListMessagesAsync", "EnumerateMessagesAsync")]
    [Arguments("ListItemsAsync", "EnumerateItemsAsync")]
    public async Task EnumerationMethodName_Should_Replace_The_List_Verb(string methodName, string expected)
    {
        await Assert.That(OperationNamePolicy.EnumerationMethodName(methodName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("GetMessagesAsync")]
    [Arguments("ListMessages")]
    public async Task EnumerationMethodName_Should_Return_Null_Outside_Async_List_Methods(string methodName)
    {
        await Assert.That(OperationNamePolicy.EnumerationMethodName(methodName)).IsNull();
    }

    [Test]
    [Arguments("health.get", "get", "HealthResponse")]
    [Arguments("session.message", "get", "SessionMessageResponse")]
    [Arguments("session.get", "get", "SessionResponse")]
    [Arguments("session.list", "get", "SessionListResponse")]
    [Arguments("session.create", "post", "SessionCreateResponse")]
    [Arguments("message.list", "get", "MessageListResponse")]
    [Arguments("session.remove", "delete", "SessionRemoveResponse")]
    [Arguments("shell.timeout", "patch", "ShellTimeoutResponse")]
    [Arguments("shell.output", "get", "ShellOutputResponse")]
    [Arguments("shell.get", "get", "ShellResponse")]
    [Arguments("session.update", "patch", "SessionUpdateResponse")]
    [Arguments("session.prompt", "post", "SessionPromptResponse")]
    [Arguments("pty.update", "put", "PtyUpdateResponse")]
    [Arguments("integration.oauth.complete", "post", "IntegrationOAuthCompleteResponse")]
    [Arguments("experimental.session.instructions.entry.put", "put", "ExperimentalSessionInstructionsEntryResponse")]
    [Arguments("location.reload", "post", "LocationReloadResponse")]
    public async Task ResponseTypeName_Should_Carry_Only_A_Grammar_Verb(string operationId, string method, string expected)
    {
        await Assert.That(OperationNamePolicy.ResponseTypeName(Operation(operationId, method))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("health.get", "get", "HealthData")]
    [Arguments("session.message", "get", "SessionMessageData")]
    [Arguments("session.create", "post", "SessionCreateData")]
    [Arguments("shell.timeout", "patch", "ShellTimeoutData")]
    public async Task PayloadTypeName_Should_Replace_The_Response_Spine_Suffix(string operationId, string method,
        string expected)
    {
        await Assert.That(OperationNamePolicy.PayloadTypeName(Operation(operationId, method))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("health.get", "get", false, "Get")]
    [Arguments("session.message", "get", true, "GetMessage")]
    [Arguments("session.get", "get", true, "GetSession")]
    [Arguments("session.list", "get", true, "ListSessions")]
    [Arguments("session.create", "post", true, "CreateSession")]
    [Arguments("message.list", "get", true, "ListMessages")]
    public async Task RouteMemberName_Should_Fall_Back_To_The_Group_On_Client_Placement_Only(string operationId,
        string method, bool clientPlacement, string expected)
    {
        var placement = clientPlacement ? GroupPlacement.Client : GroupPlacement.Root;

        await Assert.That(OperationNamePolicy.RouteMemberName(Operation(operationId, method), placement)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("health.get", "get", "Health")]
    [Arguments("session.message", "get", "Message")]
    [Arguments("session.get", "get", "Session")]
    [Arguments("session.list", "get", "Sessions")]
    [Arguments("session.create", "post", "Session")]
    [Arguments("message.list", "get", "Messages")]
    public async Task PayloadName_Should_Pluralize_The_Group_For_List_Operations(string operationId, string method,
        string expected)
    {
        await Assert.That(OperationNamePolicy.PayloadName(Operation(operationId, method))).IsEqualTo(expected);
    }

    [Test]
    public async Task PayloadName_Should_Return_Null_When_The_Group_Cannot_Be_Pluralized()
    {
        await Assert.That(OperationNamePolicy.PayloadName(Operation("boss.list"))).IsNull();
    }

    [Test]
    [Arguments("session.list", "get", "SessionListRequest")]
    [Arguments("message.list", "get", "MessageListRequest")]
    [Arguments("session.create", "post", "SessionCreateRequest")]
    [Arguments("session.rename", "post", "SessionRenameRequest")]
    [Arguments("shell.timeout", "patch", "ShellTimeoutRequest")]
    [Arguments("shell.output", "get", "ShellOutputRequest")]
    [Arguments("shell.get", "get", "ShellRequest")]
    [Arguments("session.prompt", "post", "SessionPromptRequest")]
    [Arguments("session.environment", "put", "SessionEnvironmentRequest")]
    public async Task RequestTypeName_Should_Compose_Group_Subject_And_Grammar_Verb(string operationId, string method,
        string expected)
    {
        await Assert.That(OperationNamePolicy.RequestTypeName(Operation(operationId, method))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("session.get", "get", "Get")]
    [Arguments("session.update", "patch", "Update")]
    [Arguments("session.message.get", "get", "GetMessage")]
    public async Task RouteMemberName_Should_Mirror_The_Handle_Method_Name(string operationId, string method, string expected)
    {
        await Assert.That(OperationNamePolicy.RouteMemberName(Operation(operationId, method), GroupPlacement.Client, handle: true))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task RouteMemberName_Should_Return_Null_For_A_Non_Get_Without_A_Grammar_Verb_Or_Row()
    {
        await Assert.That(OperationNamePolicy.RouteMemberName(Operation("session.prompt", "post"), GroupPlacement.Client)).IsNull();
    }

    private static SpecOperation Operation(string operationId, string method = "get")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        return new SpecOperation
        {
            OperationId = operationId,
            Segments = [.. operationId.Split('.')],
            Method = method,
            Path = "/api/x",
            HasWildcardPath = false,
            IsWebSocket = false,
            IsSse = false,
            IsDeprecated = false,
            Parameters = [],
            Responses = [],
        };
    }
}
