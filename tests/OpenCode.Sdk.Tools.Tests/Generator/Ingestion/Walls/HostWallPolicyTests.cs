using OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Walls;

public sealed class HostWallPolicyTests
{
    [Test]
    public async Task Project_Should_Refuse_Path_Level_Parameters()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithRawTopLevelFromFixture("paths", "path-level-parameters.json"));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("path-level parameters");
        await Assert.That(ex.Message).Contains("/api/items/{id}");
    }

    [Test]
    public async Task Project_Should_Refuse_Unsupported_Method()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.head", method: "head"));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("method 'head'");
    }

    [Test]
    public async Task Project_Should_Refuse_Cookie_Parameter()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.cookie", configure: operation => operation
            .Parameter("session", "cookie", schema => schema.Type("string"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("cookie");
        await Assert.That(ex.Message).Contains("parameters/0");
    }

    [Test]
    public async Task Project_Should_Refuse_Form_Style_Parameter()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.form", configure: operation => operation
            .ParameterWithStyle("filter", "form", explode: true)));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("style");
        await Assert.That(ex.Message).Contains("form");
    }

    [Test]
    public async Task Project_Should_Refuse_Content_Based_Parameter()
    {
        var host = new OperationProjectionTestHost();
        var scenario =
            SpecScenario.Define(spec => spec.WithOperation("test.content", configure: operation => operation.ContentParameter("filter")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("content");
        await Assert.That(ex.Message).Contains("parameters/0");
    }

    [Test]
    public async Task Project_Should_Refuse_Duplicate_Parameter_Identity()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.duplicate", configure: operation => operation
            .Parameter("filter", "query", schema => schema.Type("string"))
            .Parameter("filter", "query", schema => schema.Type("string"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("duplicate parameter");
        await Assert.That(ex.Message).Contains("filter");
    }

    [Test]
    public async Task Project_Should_Refuse_Request_Body_With_Multiple_Media_Types()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.body", method: "post", configure: operation => operation
            .RequestBodyMediaTypes("application/json", "text/plain")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("request body");
        await Assert.That(ex.Message).Contains("exactly one media type");
    }

    [Test]
    public async Task Project_Should_Refuse_Response_With_Multiple_Media_Types()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.response", configure: operation => operation
            .ResponseMediaTypes(200, "application/json", "text/plain")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("response content");
        await Assert.That(ex.Message).Contains("at most one media type");
    }

    [Test]
    public async Task Project_Should_Refuse_Effect_Stream_On_Json_Media()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.effect", configure: operation => operation
            .JsonResponseWithEffectStreamFromFixture("effect-stream.json")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("x-effect-stream");
        await Assert.That(ex.Message).Contains("text/event-stream");
    }

    [Test]
    public async Task Project_Should_Refuse_Default_Response_Key()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.default", configure: operation => operation.DefaultResponse()));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("default");
        await Assert.That(ex.Message).Contains("integer status");
    }

    [Test]
    public async Task Project_Should_Locate_Malformed_Media_Type()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.media", configure: operation => operation
            .Response(200, "application-json", schema => schema.Type("string"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("application-json");
        await Assert.That(ex.Message).Contains("responses/200/content");
    }

    [Test]
    public async Task Project_Should_Ignore_Unknown_Operation_Extension()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.extension", configure: operation => operation
            .Extension("x-unknown", "true")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations).Count().IsEqualTo(1);
    }
}
