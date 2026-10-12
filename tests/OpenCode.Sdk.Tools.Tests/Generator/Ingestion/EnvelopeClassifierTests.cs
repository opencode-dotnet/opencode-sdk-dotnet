using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

public sealed class EnvelopeClassifierTests
{
    [Test]
    public async Task Classify_Should_Recognize_Data_Envelope_Through_Named_Reference()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithRawSchema("DataEnvelope", "envelope-data.json")
            .WithOperation("test.data", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("DataEnvelope"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.Data);
    }

    [Test]
    public async Task Classify_Should_Recognize_DataLocation_Envelope_Through_Named_Reference()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithRawSchema("LocatedEnvelope", "envelope-data-location.json")
            .WithOperation("test.location", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("LocatedEnvelope"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.DataLocation);
    }

    [Test]
    public async Task Classify_Should_Recognize_CursorData_Envelope_From_Inline_Fixture()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.cursor", configure: operation => operation
            .ResponseFromFixture(200, "application/json", "envelope-cursor-data.json")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.CursorData);
    }

    [Test]
    public async Task Classify_Should_Recognize_DataHasMore_Envelope_From_Inline_Fixture()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.more", configure: operation => operation
            .ResponseFromFixture(200, "application/json", "envelope-data-has-more.json")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.DataHasMore);
    }

    [Test]
    public async Task Classify_Should_Recognize_SingleKey_Envelope_From_An_Inline_Body()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.handoff", configure: operation => operation
            .ResponseFromFixture(200, "application/json", "envelope-single-key.json")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.SingleKey);
    }

    [Test]
    public async Task Classify_Should_Not_Recognize_SingleKey_Through_A_Named_Component()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithRawSchema("SingleKeyComponent", "envelope-single-key.json")
            .WithOperation("test.component", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("SingleKeyComponent"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.Bare);
    }

    [Test]
    public async Task Classify_Should_Return_Bare_For_Unrecognized_Json_Shape()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.bare", configure: operation => operation
            .Response(200, "application/json", schema => schema.Type("string"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.Bare);
    }

    [Test]
    public async Task Classify_Should_Return_None_For_Response_Without_Content()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.none", configure: operation => operation.Response(204)));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.None);
    }

    [Test]
    public async Task Classify_Should_Not_Recognize_Envelope_Superset()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("test.superset", configure: operation => operation
            .Response(200, "application/json", schema => schema
                .Type("object")
                .Property("data", property => property.Type("string"))
                .Property("extra", property => property.Type("string")))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Operations[0].Responses[0].EnvelopeShape).IsEqualTo(SpecEnvelopeShape.Bare);
    }

    [Test]
    public async Task Classify_Should_Refuse_Reference_Cycle()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("First", schema => schema.Ref("Second"))
            .WithSchema("Second", schema => schema.Ref("First"))
            .WithOperation("test.cycle", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("First"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("reference cycle");
        await Assert.That(ex.Message).Contains("responses/200");
    }

    [Test]
    public async Task Classify_Should_Detect_Sse_And_Project_The_Effect_Stream_Contract()
    {
        var host = new OperationProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithOperation("session.events", configure: operation => operation
            .SseResponseFromFixture(schema => schema.Type("string"), "effect-stream.json")));

        var result = await host.ProjectAsync(scenario);

        var operation = result.Operations[0];
        var response = operation.Responses[0];
        await Assert.That(operation.IsSse).IsTrue();
        await Assert.That(response.IsSse).IsTrue();
        await Assert.That(response.ContentType!.IsEventStream).IsTrue();
        await Assert.That(response.EffectStream).IsNotNull();
        await Assert.That(response.EffectStream!.Encoding).IsEqualTo("sse");
        await Assert.That(response.EffectStream.FailureEventName).IsEqualTo("effect/http-api/stream/failure");
        await Assert.That(response.EffectStream.CauseSchema).IsTypeOf<ArrayNode>();
        await Assert.That(response.EffectStream.ErrorSchema).IsTypeOf<NeverNode>();
    }

    [Test]
    public async Task Project_Should_Refuse_A_Non_Object_Effect_Stream_Extension()
    {
        var host = new OperationProjectionTestHost();
        var scenario = new StreamOperationScenario(extensionProfile: StreamExtensionProfile.NonObject);

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Errors).Count().IsEqualTo(1);
        await Assert.That(ex.Errors[0].Location).EndsWith("/x-effect-stream");
        await Assert.That(ex.Errors[0].Problem).Contains("JSON object");
    }
}
