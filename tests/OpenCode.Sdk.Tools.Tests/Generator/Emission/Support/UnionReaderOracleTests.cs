using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<RoslynCompilationSlots>]
public sealed class UnionReaderOracleTests(PinnedBinding pinned)
{
    [Test]
    public async Task Oracle_Should_Accept_A_Reader_That_Follows_The_Contract(CancellationToken cancellationToken)
    {
        var oracle = await HandOracleAsync();
        foreach (var row in await RecordCorpusGenerator.GenerateAsync(pinned.Plan, ["ToolFileContent"]))
        {
            await oracle.CompareAsync(row);
        }
        await foreach (var row in new UnionReaderFixtureCatalog().RowsAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            await oracle.CompareAsync(row);
        }
    }

    [Test]
    public async Task Oracle_Should_Detect_A_Reader_That_Drops_A_Member()
    {
        var oracle = await HandOracleAsync("Name = name", "Name = null");
        var row = (await RecordCorpusGenerator.GenerateAsync(pinned.Plan, ["ToolFileContent"]))
            .Single(static row => row.Name == "tool-content/file/maximal");
        var actual = await AssertDifferenceAsync(oracle, row, "members");
        using var members = JsonDocument.Parse(actual.Bytes);
        await Assert.That(members.RootElement.EnumerateObject().Select(static member => member.Name))
            .IsEquivalentTo(["type", "uri", "mime"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Oracle_Should_Detect_A_Dispatch_To_The_Wrong_Variant()
    {
        var oracle = await HandOracleAsync("ToolFileContentHandReader.Read(ref reader, options)",
            "JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.ToolTextContent)");
        var row = (await RecordCorpusGenerator.GenerateAsync(pinned.Plan, ["ToolFileContent"]))
            .Single(static row => row.Name == "tool-content/file/maximal");
        var extra = await RecordCorpusGenerator.ElementAsync(static writer => writer.WriteStringValue("wrong-arm-text"));
        row = row with { Json = await RecordCorpusGenerator.VaryAsync(row.Json, "text", "append", extra) };
        var actual = await AssertDifferenceAsync(oracle, row, "type");
        await Assert.That(actual.Type).IsEqualTo("OpenCode.Sdk.Models.ToolTextContent");
    }

    [Test]
    public async Task Oracle_Should_Compare_Every_Selected_Corpus_Row_And_Accept_Every_Seed()
    {
        var names = pinned.Plan.Unions.Where(static union => union.Name is "IToolContent" or "ISessionMessageToolState")
            .SelectMany(static union => union.Variants).Select(static arm => arm.TypeName).Order(StringComparer.Ordinal).ToArray();
        var rows = await RecordCorpusGenerator.GenerateAsync(pinned.Plan, names);
        await Assert.That(rows.Select(static row => row.RootType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            .IsEquivalentTo(names, CollectionOrdering.Matching);
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan);
        foreach (var row in rows)
        {
            await oracle.CompareAsync(row);
            if (row.Name.EndsWith("/tag/oracle.future", StringComparison.Ordinal))
            {
                var union = pinned.Plan.Unions.Single(union => union.Name == row.UnionType);
                var unknown = UnionReaderOracle.Observe(oracle.Metadata, union.Name, row.Json);
                await Assert.That(unknown.Type).IsEqualTo(union.Namespace + "." + union.UnknownTypeName);
                await Assert.That(unknown.Bytes).IsEquivalentTo(row.Json, CollectionOrdering.Matching);
            }
            if (row.IsSeed)
            {
                await Assert.That(UnionReaderOracle.Observe(oracle.Metadata, row.RootType, row.Json).Phase)
                    .IsEqualTo("success").Because(row.Name);
            }
        }
    }

    [Test]
    public async Task Oracle_Should_Accept_Prefix_And_Boolean_Corpus_Seeds()
    {
        var unions = pinned.Plan.Unions.Where(static union => union.MarkerKind == Tools.Generator.Ingestion.Models.LiteralKind.Boolean || union.PrefixVariant is not null).ToArray();
        var names = unions.SelectMany(static union => union.PrefixVariant is { } prefix
            ? [prefix.TypeName] : union.Variants.Select(static arm => arm.TypeName)).ToArray();
        var rows = await RecordCorpusGenerator.GenerateAsync(pinned.Plan, names);
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan);
        foreach (var row in rows)
        {
            await oracle.CompareAsync(row);
            if (!row.IsSeed)
            {
                continue;
            }
            await Assert.That(row.UnionType).IsNotNull().Because(row.Name);
            var union = unions.Single(union => union.Name == row.UnionType);
            using var value = JsonDocument.Parse(row.Json);
            if (union.PrefixVariant is { } prefix)
            {
                await Assert.That(value.RootElement.GetProperty(union.MarkerWireName).GetString()).IsEqualTo(prefix.Prefix + "oracle");
            }
            else
            {
                await Assert.That(value.RootElement.GetProperty(union.MarkerWireName).GetBoolean())
                    .IsEqualTo(bool.Parse(union.Variants.Single(arm => arm.TypeName == row.RootType).Tag));
            }
            await Assert.That(UnionReaderOracle.Observe(oracle.Metadata, row.UnionType!, row.Json).Phase).IsEqualTo("success");
        }
    }

    [Test]
    public async Task Catalog_Should_Name_Every_Linked_Serialization_Resource()
    {
        var discovered = typeof(UnionReaderOracleTests).Assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith("OpenCode.Sdk.Tests.Fixtures.Serialization.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        await Assert.That(discovered).IsEquivalentTo(UnionReaderFixtureCatalog.ResourceNames.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
        await Assert.That(UnionReaderFixtureCatalog.NonMaterialization.Keys).IsEquivalentTo(["first-match-expected"], CollectionOrdering.Matching);
        await Assert.That(UnionReaderFixtureCatalog.NonMaterialization["first-match-expected"])
            .IsEqualTo("Expected arm indices annotate first-match-corpus; this is not an independent wire fixture.");
    }

    [Test]
    public async Task Oracle_Should_Detect_Every_Complete_Refusal_Field_And_The_Phase()
    {
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan);
        var row = (await RecordCorpusGenerator.GenerateAsync(pinned.Plan, ["ToolFileContent"]))
            .Single(static row => row.Name == "tool-content/file/missing/uri");
        var expected = UnionReaderOracle.Observe(oracle.Metadata, "IToolContent", row.Json);
        await Assert.That(expected.Phase).IsEqualTo("read");
        _ = await Assert.That(async () => await oracle.CompareAsync(row with { IsSeed = true })).Throws<Exception>();
        foreach (var changed in new[] { expected with { Phase = "write" }, expected with { ExceptionType = "other" },
                     expected with { Path = "other" }, expected with { Message = expected.Message + "other" },
                     expected with { Line = 17 }, expected with { BytePosition = 17 }, expected with { Consumed = 17 },
                     expected with { Token = JsonTokenType.True }, expected with { Depth = 17 },
                     expected with { TokenStart = 17 }, expected with { SequenceOffset = 17 } })
        {
            _ = await Assert.That(async () => await UnionReaderOracle.AssertEquivalentAsync(changed, expected, row.Name)).Throws<Exception>();
        }
    }

    private static async Task<UnionReaderObservation> AssertDifferenceAsync(UnionReaderOracle oracle, RecordCorpusRow row, string difference)
    {
        var actual = UnionReaderOracle.Observe(oracle.Product, "IToolContent", row.Json);
        var expected = UnionReaderOracle.Observe(oracle.Metadata, "IToolContent", row.Json);
        await Assert.That(actual.Phase).IsEqualTo("success");
        await Assert.That(expected.Phase).IsEqualTo("success");
        await Assert.That(expected.Type).IsEqualTo("OpenCode.Sdk.Models.ToolFileContent");
        var failure = await Assert.That(async () => await UnionReaderOracle.AssertEquivalentAsync(actual, expected, row.Name)).Throws<Exception>();
        await Assert.That(failure!.Message).Contains(row.Name + "/" + difference);
        return actual;
    }

    private Task<UnionReaderOracle> HandOracleAsync(string? before = null, string? after = null)
    {
        var fixture = new FixtureLoader().Load("Serialization.ToolFileContentHandReader.cs.txt");
        if (before is not null)
        {
            fixture = fixture.Replace(before, after, StringComparison.Ordinal);
        }
        var sources = SourceEmitter.Emit(pinned.Plan).Select(source => source.RelativePath.EndsWith("/ToolContentJsonConverter.cs", StringComparison.Ordinal)
            ? MetadataUnionOracleEmitter.Source(source.RelativePath, fixture) : source).ToArray();
        return UnionReaderOracle.CreateAsync(pinned.Plan, sources);
    }
}
