using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class StreamOperationEmissionTests
{
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Bind_Source_Generate_And_Compile_A_Stream_Operation()
    {
        var plan = await EmitterPlanFixture.CreateStreamPlanAsync();
        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();

        await Assert.That(operation.MethodName).IsEqualTo("GetEventsAsync");
        await Assert.That(operation.HttpMethod).IsEqualTo("get");
        await Assert.That(operation.RouteTemplate).IsEqualTo("/api/example/{exampleID}/events");
        await Assert.That(operation.RouteContainerName).IsEqualTo("Example");
        await Assert.That(operation.RouteMemberName).IsEqualTo("GetEvents");
        await Assert.That(operation.Envelope).IsNull();
        await Assert.That(operation.RequestBody).IsNull();
        await Assert.That(operation.QueryRequest).IsNull();
        await Assert.That(operation.Stream).IsNotNull();
        await Assert.That(operation.Stream!.PayloadTypeName).IsEqualTo("ExampleEvent");
        await Assert.That(operation.Stream.AdapterTypeName).IsEqualTo("ExampleEventsResponseStreamAdapter");
        await Assert.That(operation.Stream.FailureEventName).IsEqualTo(StreamOperationScenario.FailureEventName);
        await Assert.That(operation.Stream.CauseTypeName).IsEqualTo("IStreamFailureCause[]");
        await Assert.That(operation.Parameters.Single().Name).IsEqualTo("exampleId");

        (int Status, IReadOnlyList<string> Tags)[] expectedStatuses =
        [
            (400, ["ExampleBadRequestError"]),
            (401, ["ExampleUnauthorizedError"]),
            (404, ["ExampleGoneError", "ExampleNotFoundError"]),
        ];
        foreach (var expected in expectedStatuses)
        {
            var status = operation.ErrorMap.Statuses.Single(candidate => candidate.StatusCode == expected.Status);
            await Assert.That(status.Tags.Select(static tag => tag.Tag).SequenceEqual(expected.Tags, StringComparer.Ordinal)).IsTrue();
        }

        var sources = SourceEmitter.Emit(plan);
        var clientSource = EmitterSnapshot.Create(
            [sources.Single(static source => source.RelativePath == "OpenCodeClient.cs")]);
        var adapterSource = EmitterSnapshot.Create(
            [sources.Single(static source => source.RelativePath == "Example/Internal/ExampleEventsResponseStreamAdapter.cs")]);
        var causeConverterSource = EmitterSnapshot.Create(
            [sources.Single(static source => source.RelativePath == "Example/Internal/StreamFailureCauseJsonConverter.cs")]);
        var routeSource = EmitterSnapshot.Create(
            [sources.Single(static source => source.RelativePath == "Example/Internal/OpenCodeRoutes.Example.cs")]);
        await Assert
            .That(clientSource)
            .Contains(
                "public virtual IAsyncEnumerable<ExampleEvent> GetEventsAsync(string exampleId, CancellationToken cancellationToken = default)");
        await Assert
            .That(clientSource)
            .Contains(
                "return Pipeline.ExecuteStreamAsync(\"example.events\", HttpMethod.Get, OpenCodeRoutes.Example.GetEvents(exampleId), ExampleEventsResponseStreamAdapter.Instance, cancellationToken);");
        await Assert.That(clientSource).DoesNotContain("ResponseAdapter.Instance");
        await Assert.That(clientSource).DoesNotContain("requestOptions");
        await Assert.That(clientSource).DoesNotContain("NoThrow");
        await Assert.That(clientSource).Contains("OpenCodeStreamFailureException");
        await Assert.That(clientSource).Contains("schema-valid failure with a typed cause");
        await Assert.That(clientSource).Contains("The example stream is volatile by contract.");
        await Assert.That(adapterSource).Contains("public string FailureEventName => \"effect/http-api/stream/failure\";");
        await Assert
            .That(adapterSource)
            .Contains(
                "public JsonTypeInfo<ExampleEvent> PayloadTypeInfo => OpenCodeJsonContext.Default.ExampleEvent;");
        await Assert.That(adapterSource).Contains("public JsonTypeInfo<IStreamFailureCause[]> CauseTypeInfo");
        await Assert.That(causeConverterSource).Contains("[\"Fail\"] = null");
        await Assert.That(causeConverterSource).DoesNotContain("typeof(StreamFailureCauseFail)");
        await Assert.That(adapterSource).Contains("private static readonly string[] Status400Tags = [\"ExampleBadRequestError\"];");
        await Assert.That(adapterSource).Contains("private static readonly string[] Status401Tags = [\"ExampleUnauthorizedError\"];");
        await Assert.That(routeSource).Contains("namespace OpenCode.Sdk.Internal;");
        await Assert.That(routeSource).Contains("RouteValuePolicy.Escape(exampleId, nameof(exampleId))");
        await Assert
            .That(adapterSource)
            .Contains(
                "private static readonly string[] Status404Tags = [\"ExampleGoneError\", \"ExampleNotFoundError\"];");

        var diagnostics = await GeneratedSourceCompiler.CompileWithSdkCoreAsync(sources);
        await Assert.That(diagnostics).IsEmpty();
    }
}
