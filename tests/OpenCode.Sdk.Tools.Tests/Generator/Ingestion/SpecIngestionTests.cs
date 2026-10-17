using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

public sealed class SpecIngestionTests
{
    [Test]
    public async Task IngestAsync_Should_Produce_Operations_And_Schemas()
    {
        var context = SpecScenario.Define(spec => spec
            .WithSchema("Session", schema => schema.Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("session.get", path: "/api/session/{sessionID}", configure: operation => operation
                .Parameter("sessionID", "path", parameter => parameter.Type("string"), required: true)
                .Response(200, "application/json", schema => schema.Ref("Session"))))
            .Build();

        var document = await new SpecIngestion(context.FileSystem).IngestAsync(context.SpecPath, CancellationToken.None);

        await Assert.That(document.Operations).Count().IsEqualTo(1);
        await Assert.That(document.Operations[0].OperationId).IsEqualTo("session.get");
        await Assert.That(document.Operations[0].Segments).IsEquivalentTo(["session", "get"], CollectionOrdering.Matching);
        await Assert.That(document.Schemas["Session"]).IsTypeOf<ObjectNode>();
    }

    [Test]
    public async Task IngestAsync_Should_Refuse_Ref_With_Constraint_Sibling()
    {
        var context = SpecScenario.Define(spec => spec
            .WithSchema("Session", schema => schema.Type("object"))
            .WithSchema("Alias", schema => schema.Ref("Session").Raw("pattern", "\"^ses_\"")))
            .Build();

        var ex = await Assert
            .That(async () => _ = await new SpecIngestion(context.FileSystem).IngestAsync(context.SpecPath, CancellationToken.None))
            .Throws<IngestionException>();

        await Assert.That(ex!.Message).Contains("$ref");
        await Assert.That(ex.Message).Contains("pattern");
    }

    [Test]
    public async Task IngestAsync_Should_Admit_Ref_With_Description_Sibling()
    {
        var context = SpecScenario.Define(spec => spec
            .WithSchema("Session", schema => schema.Type("object"))
            .WithSchema("Holder", schema => schema.Type("object")
                .Property("session", property => property.Ref("Session").Description("The session."))))
            .Build();

        var document = await new SpecIngestion(context.FileSystem).IngestAsync(context.SpecPath, CancellationToken.None);

        await Assert.That(document.Schemas.Keys).Contains("Holder");
    }

    [Test]
    public async Task IngestAsync_Should_Not_Flag_Property_Named_Ref()
    {
        var context = SpecScenario.Define(spec => spec
            .WithSchema("Bag", schema => schema.Type("object")
                .Property("$ref", property => property.Type("string"))
                .Property("other", property => property.Type("number"))))
            .Build();

        var document = await new SpecIngestion(context.FileSystem).IngestAsync(context.SpecPath, CancellationToken.None);

        await Assert.That(document.Schemas["Bag"]).IsTypeOf<ObjectNode>();
    }

    [Test]
    public async Task CreateServices_Should_Resolve_Spec_Ingestion_Seam()
    {
        var context = SpecScenario.Define(_ => { }).Build();
        using var provider = ToolApp
            .CreateServices(services => services.AddSingleton(context.FileSystem))
            .BuildServiceProvider();

        var ingestion = provider.GetRequiredService<ISpecIngestion>();
        var document = await ingestion.IngestAsync(
            context.SpecPath,
            new Dictionary<string, string>(StringComparer.Ordinal),
            CancellationToken.None);

        await Assert.That(provider.GetRequiredService<IFileSystem>()).IsSameReferenceAs(context.FileSystem);
        await Assert.That(document.Operations).IsEmpty();
    }
}
