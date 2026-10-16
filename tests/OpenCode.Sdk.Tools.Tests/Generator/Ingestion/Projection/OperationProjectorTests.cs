using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Projection;

public sealed class OperationProjectorTests
{
    [Test]
    public async Task Project_Should_Preserve_Order_And_Derive_Segments()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithOperation("session.list", path: "/api/session")
            .WithOperation("agent.list", path: "/api/agent"));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations).Count().IsEqualTo(2);
        await Assert.That(result.Operations[0].OperationId).IsEqualTo("session.list");
        await Assert.That(result.Operations[0].Segments[0]).IsEqualTo("session");
        await Assert.That(result.Operations[0].Segments[1]).IsEqualTo("list");
        await Assert.That(result.Operations[1].OperationId).IsEqualTo("agent.list");
        await Assert.That(result.Operations[1].Segments[0]).IsEqualTo("agent");
        await Assert.That(result.Operations[1].Segments[1]).IsEqualTo("list");
    }

    [Test]
    public async Task Project_Should_Refuse_An_Unrepaired_Group_Qualified_Identity()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("server.experimental.persistentPty.list", path: "/api/experimental/pty"));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("group/action convention");
        await Assert.That(ex.Message).Contains("server.experimental.persistentPty.list");
    }

    [Test]
    public async Task Project_Should_Preserve_Deep_Operation_Segments()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.revert.stage"));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Segments).Count().IsEqualTo(3);
        await Assert.That(result.Operations[0].Segments[0]).IsEqualTo("session");
        await Assert.That(result.Operations[0].Segments[1]).IsEqualTo("revert");
        await Assert.That(result.Operations[0].Segments[2]).IsEqualTo("stage");
    }

    [Test]
    public async Task Project_Should_Record_Trailing_Wildcard_Path()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("fs.read", path: "/api/fs/read/*"));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].HasWildcardPath).IsTrue();
        await Assert.That(result.Operations[0].Path).IsEqualTo("/api/fs/read/*");
    }

    [Test]
    public async Task Project_Should_Refuse_Non_Trailing_Wildcard_Path()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("fs.read", path: "/api/*/read"));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("wildcard");
        await Assert.That(ex.Message).Contains("/api/*/read");
    }

    [Test]
    public async Task Project_Should_Record_WebSocket_Flag()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("pty.connect", configure: operation =>
            operation.Extension("x-websocket", "true")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].IsWebSocket).IsTrue();
    }

    [Test]
    public async Task Project_Should_Record_Deprecation_And_Documentation()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.old", configure: operation => operation
            .Deprecated()
            .Summary("Old operation")
            .Description("Use the replacement.")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].IsDeprecated).IsTrue();
        await Assert.That(result.Operations[0].Summary).IsEqualTo("Old operation");
        await Assert.That(result.Operations[0].Description).IsEqualTo("Use the replacement.");
    }

    [Test]
    public async Task Project_Should_Ignore_Known_Operation_Metadata()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.list", configure: operation => operation
            .WithIgnoredTagsAndSecurity()
            .Extension("x-codeSamples", "[]")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Project_Should_Refuse_Duplicate_OperationId()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithOperation("session.get", path: "/api/session/one")
            .WithOperation("session.get", path: "/api/session/two"));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("duplicate operationId");
        await Assert.That(ex.Message).Contains("session.get");
    }

    [Test]
    [Arguments("get")]
    [Arguments("post")]
    [Arguments("put")]
    [Arguments("patch")]
    [Arguments("delete")]
    public async Task Project_Should_Admit_Supported_Methods(string method)
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.method", method));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Method).IsEqualTo(method);
    }

    [Test]
    public async Task Project_Should_Project_DeepObject_Parameter()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("search.list", configure: operation => operation
            .Parameter("filter", "query", schema => schema.Type("object").EmptyProperties(), deepObject: true)));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Parameters[0].IsDeepObject).IsTrue();
        await Assert.That(result.Operations[0].Parameters[0].Location).IsEqualTo(SpecParameterLocation.Query);
    }

    [Test]
    public async Task Project_Should_Project_Header_Parameter()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("pty.token", configure: operation => operation
            .Parameter("x-opencode-ticket", "header", schema => schema.Type("string"), required: true)));

        var result = await host.ProjectAsync(scenario);

        var parameter = result.Operations[0].Parameters[0];
        await Assert.That(parameter.Location).IsEqualTo(SpecParameterLocation.Header);
        await Assert.That(parameter.Name).IsEqualTo("x-opencode-ticket");
        await Assert.That(parameter.IsRequired).IsTrue();
    }

    [Test]
    public async Task Project_Should_Preserve_Parameter_Order_And_Bracketed_Name()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("search.list", configure: operation => operation
            .Parameter("query", "query", schema => schema.Type("string"))
            .Parameter("filter[status]", "query", schema => schema.Type("string"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Parameters).Count().IsEqualTo(2);
        await Assert.That(result.Operations[0].Parameters[0].Name).IsEqualTo("query");
        await Assert.That(result.Operations[0].Parameters[1].Name).IsEqualTo("filter[status]");
    }

    [Test]
    public async Task Project_Should_Project_Boolean_Enum_Parameter_As_Union()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("search.list", configure: operation => operation
            .Parameter("enabled", "query", schema => schema.AnyOf(
                branch => branch.Type("boolean"),
                branch => branch.Type("boolean").BooleanEnum(true)))));

        var result = await host.ProjectAsync(scenario);

        var reference = (RefNode)result.Operations[0].Parameters[0].Schema;
        await Assert.That(result.Schemas[reference.Target]).IsTypeOf<UnionNode>();
    }

    [Test]
    public async Task Project_Should_Project_Required_Request_Body()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.create", method: "post", configure: operation => operation
            .RequestBody("application/json", schema => schema.Type("string"), required: true)));

        var result = await host.ProjectAsync(scenario);

        var requestBody = result.Operations[0].RequestBody;
        await Assert.That(requestBody).IsNotNull();
        await Assert.That(requestBody!.ContentType.Stripped).IsEqualTo("application/json");
        await Assert.That(requestBody.IsRequired).IsTrue();
        await Assert.That(requestBody.Schema).IsTypeOf<PrimitiveNode>();
    }

    [Test]
    public async Task Project_Should_Refuse_Undeclared_Path_Token()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.get", path: "/api/session/{sessionID}"));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("sessionID");
        await Assert.That(ex.Message).Contains("not declared");
    }

    [Test]
    public async Task Project_Should_Refuse_Path_Parameter_Absent_From_Template()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.get", path: "/api/session", configure: operation => operation
            .Parameter("sessionID", "path", schema => schema.Type("string"), required: true)));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("sessionID");
        await Assert.That(ex.Message).Contains("path");
    }

    [Test]
    public async Task Project_Should_Sort_Responses_By_Status_Code()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.get", configure: operation => operation
            .Response(404)
            .Response(200)));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses).Count().IsEqualTo(2);
        await Assert.That(result.Operations[0].Responses[0].StatusCode).IsEqualTo(200);
        await Assert.That(result.Operations[0].Responses[1].StatusCode).IsEqualTo(404);
    }
}
