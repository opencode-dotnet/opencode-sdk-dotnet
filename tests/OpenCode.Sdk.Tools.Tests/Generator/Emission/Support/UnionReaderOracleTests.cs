using System.Text.Json;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Exceptions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<RoslynCompilationSlots>]
public sealed class UnionReaderOracleTests(PinnedBinding pinned)
{
    [Test]
    public async Task Oracle_Should_Isolate_Completed_Content_And_Override_Exactly_The_Selected_Unions()
    {
        var emitted = SourceEmitter.Emit(pinned.Plan);
        var sources = emitted.Select(source => source.RelativePath switch
        {
            "Sessions/Internal/ToolFileContentJsonReader.cs" => MetadataUnionOracleEmitter.Source(source.RelativePath,
                System.Text.Encoding.UTF8.GetString(source.Utf8Source.Span)
                    .Replace("Name = state.value2", "Name = state.value2 is null ? null : throw new JsonException(\"product-reader-entered\")", StringComparison.Ordinal)),
            "Sessions/Internal/SessionTextDeltaDataJsonReader.cs" => MetadataUnionOracleEmitter.Source(source.RelativePath,
                System.Text.Encoding.UTF8.GetString(source.Utf8Source.Span)
                    .Replace("Delta = state.value3!", "Delta = state.value3 is null ? string.Empty : throw new JsonException(\"product-reader-entered\")", StringComparison.Ordinal)),
            "Sessions/Internal/SessionMessageAssistantToolJsonReader.cs" => MetadataUnionOracleEmitter.Source(source.RelativePath,
                System.Text.Encoding.UTF8.GetString(source.Utf8Source.Span)
                    .Replace("Name = state.value1!", "Name = state.value1 is null ? string.Empty : throw new JsonException(\"product-reader-entered\")", StringComparison.Ordinal)),
            "Permissions/Internal/PermissionAskedDataJsonReader.cs" => MetadataUnionOracleEmitter.Source(source.RelativePath,
                System.Text.Encoding.UTF8.GetString(source.Utf8Source.Span)
                    .Replace("Id = state.value0!", "Id = state.value0 is null ? string.Empty : throw new JsonException(\"product-reader-entered\")", StringComparison.Ordinal)),
            _ => source,
        }).ToList();
        foreach (var name in new[] { "ToolFileContent", "SessionTextDeltaData", "SessionMessageAssistantTool", "PermissionAskedData" })
        {
            var relativePath = pinned.Plan.Placement.HomeOf(name).Slice + "/Internal/" + name + "JsonReader.cs";
            await Assert.That(System.Text.Encoding.UTF8.GetString(sources.Single(source => source.RelativePath == relativePath).Utf8Source.Span))
                .IsNotEqualTo(System.Text.Encoding.UTF8.GetString(emitted.Single(source => source.RelativePath == relativePath).Utf8Source.Span)).Because(relativePath);
        }
        AddFocusedSources(sources);
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var product = UnionReaderOracle.Context(assembly);
        var focused = Focused(assembly);
        var overrides = focused.Options.Converters.Where(converter => converter.GetType().IsGenericType
            && converter.GetType().GetGenericTypeDefinition() == assembly.GetType("OpenCode.Sdk.Tests.Serialization.Support.ToolUnionMetadataOracle`1", true)).ToArray();
        await Assert.That(overrides
            .Select(static converter => converter.GetType().GenericTypeArguments.Single().Name).Order(StringComparer.Ordinal))
            .IsEquivalentTo(pinned.Plan.ReaderArms.Where(static entry => entry.Value.Count > 0).Select(static entry => entry.Key), CollectionOrdering.Matching);
        foreach (var converter in overrides)
        {
            var union = pinned.Plan.Unions.Single(union => union.Name == converter.GetType().GenericTypeArguments.Single().Name);
            // The focused converter exposes the map from which its dispatch table is built.
            var map = (IReadOnlyDictionary<string, Type?>)converter.GetType().GetProperty("Map")!.GetValue(null)!;
            await Assert.That(map.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => new KeyValuePair<string, string?>(entry.Key, entry.Value?.Name)))
                .IsEquivalentTo(union.Variants.OrderBy(static variant => variant.Tag, StringComparer.Ordinal)
                    .Select(static variant => new KeyValuePair<string, string?>(variant.Tag, variant.TypeName)), CollectionOrdering.Matching);
            var prefix = (string?)converter.GetType().GetProperty("PrefixValue")!.GetValue(null);
            var prefixType = (Type?)converter.GetType().GetProperty("PrefixTypeValue")!.GetValue(null);
            await Assert.That(prefix).IsEqualTo(union.PrefixVariant?.Prefix);
            await Assert.That(prefixType?.Name).IsEqualTo(union.PrefixVariant?.TypeName);
        }
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan);
        var rows = await RecordCorpusGenerator.GenerateAsync(pinned.Plan, ["SessionMessageToolStateCompleted", "SessionTextDelta", "SessionMessageAssistant", "PermissionAsked"]);
        foreach (var name in new[] { "SessionMessageToolState/completed/maximal", "Event/session.text.delta/maximal", "SessionMessageInfo/assistant/maximal", "Event/permission.asked/maximal" })
        {
            var row = rows.Single(row => row.Name == name);
            var entered = UnionReaderOracle.Observe(product, row.UnionType!, row.Json);
            await Assert.That(entered.Phase).IsEqualTo("read").Because(row.Name);
            await Assert.That(entered.Message).Contains("product-reader-entered").Because(row.Name);
            await UnionReaderOracle.AssertEquivalentAsync(UnionReaderOracle.Observe(focused, row.UnionType!, row.Json),
                UnionReaderOracle.Observe(oracle.Metadata, row.UnionType!, row.Json), row.Name);
        }
    }

    [Test]
    public async Task Oracle_Should_Not_Mirror_A_Product_Record_Member_Change()
    {
        var sources = SourceEmitter.Emit(pinned.Plan).Select(source => source.RelativePath.EndsWith("/ToolFileContent.cs", StringComparison.Ordinal)
            ? MetadataUnionOracleEmitter.Source(source.RelativePath, System.Text.Encoding.UTF8.GetString(source.Utf8Source.Span)
                .Replace("JsonPropertyName(\"name\")", "JsonPropertyName(\"unused\")", StringComparison.Ordinal)) : source).ToArray();
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan, sources);
        var row = (await RecordCorpusGenerator.GenerateAsync(pinned.Plan, ["ToolFileContent"]))
            .Single(static row => row.Name == "ToolContent/file/maximal");
        var failure = await Assert.That(async () => await oracle.CompareAsync(row)).Throws<AssertionException>();
        await Assert.That(failure!.Message).Contains(row.Name + "/ToolFileContent/members");
    }

    [Test]
    [Arguments("TokenUsageInfoCache", "Read = state.value0", "Read = state.value0 == 0 ? 0 : throw new JsonException(\"product-reader-entered\")", "SessionStepEnded", "Event/session.step.ended/maximal")]
    [Arguments("ModelRef", "Id = state.value0!", "Id = state.value0 is null ? string.Empty : throw new JsonException(\"product-reader-entered\")", "SessionMessageAssistant", "SessionMessageInfo/assistant/maximal")]
    public async Task Oracle_Should_Isolate_A_Deep_Nested_Reader(string name, string before, string after, string root, string rowName)
    {
        var emitted = SourceEmitter.Emit(pinned.Plan);
        var path = pinned.Plan.Placement.HomeOf(name).Slice + "/Internal/" + name + "JsonReader.cs";
        var original = System.Text.Encoding.UTF8.GetString(emitted.Single(source => source.RelativePath == path).Utf8Source.Span);
        var trapped = original.Replace(before, after, StringComparison.Ordinal);
        await Assert.That(trapped).IsNotEqualTo(original).Because(path);
        var sources = emitted.Select(source => source.RelativePath == path ? MetadataUnionOracleEmitter.Source(path, trapped) : source).ToList();
        AddFocusedSources(sources);
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var product = UnionReaderOracle.Context(assembly);
        var focused = Focused(assembly);
        var row = (await RecordCorpusGenerator.GenerateAsync(pinned.Plan, [root])).Single(row => row.Name == rowName);
        var entered = UnionReaderOracle.Observe(product, row.UnionType!, row.Json);
        await Assert.That(entered.Phase).IsEqualTo("read").Because(rowName);
        await Assert.That(entered.Message).Contains("product-reader-entered").Because(rowName);
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan);
        await UnionReaderOracle.AssertEquivalentAsync(UnionReaderOracle.Observe(focused, row.UnionType!, row.Json),
            UnionReaderOracle.Observe(oracle.Metadata, row.UnionType!, row.Json), rowName);
    }

    [Test]
    public async Task Oracle_Should_Accept_A_Reader_That_Follows_The_Contract(CancellationToken cancellationToken)
    {
        var oracle = await HandOracleAsync();
        await Assert.That(oracle.Product.GetType().Assembly.GetType("OpenCode.Sdk.Internal.ToolFileContentHandReader")).IsNotNull();
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
            .Single(static row => row.Name == "ToolContent/file/maximal");
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
            .Single(static row => row.Name == "ToolContent/file/maximal");
        var extra = await RecordCorpusGenerator.ElementAsync(static writer => writer.WriteStringValue("wrong-arm-text"));
        row = row with { Json = await RecordCorpusGenerator.VaryAsync(row.Json, "text", "append", extra) };
        var actual = await AssertDifferenceAsync(oracle, row, "type");
        await Assert.That(actual.Type).IsEqualTo("OpenCode.Sdk.Models.ToolTextContent");
    }

    [Test]
    public async Task Oracle_Should_Compare_Every_Selected_Corpus_Row_And_Accept_Every_Seed()
    {
        var names = pinned.Plan.ReaderRecords.Keys.ToArray();
        var rows = await RecordCorpusGenerator.GenerateAsync(pinned.Plan, names);
        await AssertCorpusRoutesAsync(pinned.Plan, rows);
        var oracle = await UnionReaderOracle.CreateAsync(pinned.Plan);
        foreach (var row in rows)
        {
            await oracle.CompareAsync(row);
            if (row.Name.Contains("/tag/", StringComparison.Ordinal))
            {
                var union = pinned.Plan.Unions.Single(union => union.Name == row.UnionType);
                await AssertTagAsync(oracle, union, row);
            }
        }
    }

    [Test]
    public async Task Oracle_Should_Cover_Every_Synthetic_Arm_Route_Without_Expecting_A_Nested_Union_Route()
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, [.. plan.ReaderRecords.Keys]);
        await AssertCorpusRoutesAsync(plan, rows);
    }

    private static async Task AssertCorpusRoutesAsync(EmitPlan plan, IReadOnlyList<RecordCorpusRow> rows)
    {
        var names = plan.ReaderArms.Values.SelectMany(static arms => arms).ToHashSet(StringComparer.Ordinal);
        await Assert.That(rows.Where(row => row.IsSeed && names.Contains(row.RootType)).Select(static row => row.RootType + "/" + row.UnionType)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            .IsEquivalentTo(plan.Unions.SelectMany(union => union.Variants.Where(arm => names.Contains(arm.TypeName, StringComparer.Ordinal))
                .Select(arm => arm.TypeName + "/" + union.Name)).Order(StringComparer.Ordinal), CollectionOrdering.Matching);
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
            if (row.Name.Contains("/tag/", StringComparison.Ordinal))
            {
                await AssertTagAsync(oracle, unions.Single(union => union.Name == row.UnionType), row);
            }
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
        foreach (var union in unions.Where(static union => union.MarkerKind == Tools.Generator.Ingestion.Models.LiteralKind.Boolean))
        {
            await AssertUnknownBooleanAsync(union);
        }
    }

    [Test]
    public async Task Catalog_Should_Name_Every_Linked_Serialization_Resource()
    {
        var fileSystem = new RealFileSystem();
        var repository = new RepositoryRoot(fileSystem).Locate();
        var covered = UnionReaderFixtureCatalog.CoveredFolders.SelectMany(folder => fileSystem.Directory.GetFiles(
                fileSystem.Path.Combine(repository, folder.Key), "*.json")
            .Select(file => folder.Value + fileSystem.Path.GetFileName(file))).Order(StringComparer.Ordinal).ToArray();
        var discovered = typeof(UnionReaderOracleTests).Assembly.GetManifestResourceNames()
            .Where(name => UnionReaderFixtureCatalog.CoveredFolders.Values.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        await Assert.That(discovered.Where(name => covered.Contains(name, StringComparer.Ordinal)))
            .IsEquivalentTo(covered, CollectionOrdering.Matching);
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
            .Single(static row => row.Name == "ToolContent/file/missing/uri");
        var expected = UnionReaderOracle.Observe(oracle.Metadata, "IToolContent", row.Json);
        await Assert.That(expected.Phase).IsEqualTo("read");
        var seedFailure = await Assert.That(async () => await oracle.CompareAsync(row with { IsSeed = true })).Throws<AssertionException>();
        await Assert.That(seedFailure!.Message).Contains(row.Name + "/seed/" + row.RootType);
        foreach (var changed in new[] { expected with { Phase = "write" }, expected with { ExceptionType = "other" },
                     expected with { Path = "other" }, expected with { Message = expected.Message + "other" },
                     expected with { Line = 17 }, expected with { BytePosition = 17 }, expected with { Consumed = 17 },
                     expected with { Token = JsonTokenType.True }, expected with { Depth = 17 },
                     expected with { TokenStart = 17 }, expected with { SequenceOffset = 17 } })
        {
            var failure = await Assert.That(async () => await UnionReaderOracle.AssertEquivalentAsync(changed, expected, row.Name)).Throws<AssertionException>();
            await Assert.That(failure!.Message).Contains(row.Name);
        }
    }

    private static void AddFocusedSources(List<GeneratedSource> sources)
    {
        var loader = new FixtureLoader();
        sources.Add(MetadataUnionOracleEmitter.Source("Internal/Serialization/MetadataUnionJsonConverter.cs", loader.Load("Serialization.MetadataUnionJsonConverter.cs")));
        sources.Add(MetadataUnionOracleEmitter.Source("ToolUnionMetadataOracle.cs", loader.Load("Serialization.ToolUnionMetadataOracle.cs")));
    }

    private static System.Text.Json.Serialization.JsonSerializerContext Focused(System.Reflection.Assembly assembly)
    {
        // These types and members are emitted into every focused-oracle image.
        var factory = assembly.GetType("OpenCode.Sdk.Tests.Serialization.Support.ToolUnionMetadataOracle`1", true)!
            .MakeGenericType(assembly.GetType("OpenCode.Sdk.Models.IToolContent", true)!);
        return (System.Text.Json.Serialization.JsonSerializerContext)factory.GetMethod("Create")!.Invoke(null, null)!;
    }

    private static async Task AssertTagAsync(UnionReaderOracle oracle, UnionPlan union, RecordCorpusRow row)
    {
        using var document = JsonDocument.Parse(row.Json);
        var marker = document.RootElement.GetProperty(union.MarkerWireName);
        // A string marker token always carries a non-null string value.
        var tag = marker.ValueKind == JsonValueKind.String ? marker.GetString()! : marker.GetRawText();
        var observed = UnionReaderOracle.Observe(oracle.Metadata, union.Name, row.Json);
        if (union.KnownImpossibleTags.Contains(tag, StringComparer.Ordinal))
        {
            await Assert.That(observed.Phase).IsEqualTo("read").Because(row.Name);
            await Assert.That(observed.ExceptionType).IsEqualTo(typeof(JsonException).FullName).Because(row.Name);
            await Assert.That(observed.Message).Contains("impossible").Because(row.Name);
        }
        else
        {
            await Assert.That(observed.Type).IsEqualTo(union.Namespace + "." + union.UnknownTypeName).Because(row.Name);
            await Assert.That(observed.Bytes).IsEquivalentTo(row.Json, CollectionOrdering.Matching).Because(row.Name);
        }
    }

    private async Task AssertUnknownBooleanAsync(UnionPlan original)
    {
        // The pin claims both boolean tokens; omitting one claim makes the unknown route reachable.
        var omitted = original.Variants[^1];
        var union = original with { Variants = [.. original.Variants.Where(arm => arm.TypeName != omitted.TypeName)] };
        var plan = pinned.Plan with { Unions = [.. pinned.Plan.Unions.Select(candidate => candidate.Name == union.Name ? union : candidate)] };
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, [union.Variants[0].TypeName]);
        var row = rows.Single(row => row.Name.EndsWith("/tag/" + omitted.Tag, StringComparison.Ordinal));
        var oracle = await UnionReaderOracle.CreateAsync(plan);
        await oracle.CompareAsync(row);
        await AssertTagAsync(oracle, union, row);
    }

    private static async Task<UnionReaderObservation> AssertDifferenceAsync(UnionReaderOracle oracle, RecordCorpusRow row, string difference)
    {
        var actual = UnionReaderOracle.Observe(oracle.Product, "IToolContent", row.Json);
        var expected = UnionReaderOracle.Observe(oracle.Metadata, "IToolContent", row.Json);
        await Assert.That(actual.Phase).IsEqualTo("success");
        await Assert.That(expected.Phase).IsEqualTo("success");
        await Assert.That(expected.Type).IsEqualTo("OpenCode.Sdk.Models.ToolFileContent");
        var failure = await Assert.That(async () => await UnionReaderOracle.AssertEquivalentAsync(actual, expected, row.Name)).Throws<AssertionException>();
        await Assert.That(failure!.Message).Contains(row.Name + "/" + difference);
        return actual;
    }

    private async Task<UnionReaderOracle> HandOracleAsync(string? before = null, string? after = null)
    {
        var fixture = new FixtureLoader().Load("Serialization.ToolFileContentHandReader.cs.txt");
        if (before is not null)
        {
            fixture = fixture.Replace(before, after, StringComparison.Ordinal);
        }
        var sources = SourceEmitter.Emit(pinned.Plan).Select(source => source.RelativePath.EndsWith("/ToolContentJsonConverter.cs", StringComparison.Ordinal)
            ? MetadataUnionOracleEmitter.Source(source.RelativePath, fixture) : source).ToArray();
        await Assert.That(System.Text.Encoding.UTF8.GetString(sources.Single(static source =>
            source.RelativePath.EndsWith("/ToolContentJsonConverter.cs", StringComparison.Ordinal)).Utf8Source.Span)).IsEqualTo(fixture);
        return await UnionReaderOracle.CreateAsync(pinned.Plan, sources);
    }
}
