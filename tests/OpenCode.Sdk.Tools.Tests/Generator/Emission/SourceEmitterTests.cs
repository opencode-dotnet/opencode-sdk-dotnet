using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class SourceEmitterTests(PinnedBinding pinned)
{
    [Test]
    public async Task Emit_Should_Return_Ordinal_Sorted_Byte_Identical_Source()
    {
        var plan = EmitterPlanFixture.Create();

        var first = SourceEmitter.Emit(plan);
        var second = SourceEmitter.Emit(plan);

        await Assert.That(first.Select(static source => source.RelativePath))
            .IsEquivalentTo(first.Select(static source => source.RelativePath).Order(StringComparer.Ordinal), StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(second).Count().IsEqualTo(first.Count);
        await Assert.That(second.Select(static source => source.RelativePath))
            .IsEquivalentTo(first.Select(static source => source.RelativePath), StringComparer.Ordinal, CollectionOrdering.Matching);
        for (var index = 0; index < first.Count; index++)
        {
            await Assert.That(second[index].Utf8Source.ToArray())
                .IsEquivalentTo(first[index].Utf8Source.ToArray(), CollectionOrdering.Matching)
                .Because($"source {first[index].RelativePath}");
        }
    }

    /// <summary>
    /// Generated internal types share the hand-written internal namespace, and every generated file
    /// imports it, so a generated type named like a hand-written internal type is refused at emission.
    /// </summary>
    [Test]
    public async Task Emit_Should_Refuse_A_Generated_Type_Named_Like_A_HandWritten_Internal_Type()
    {
        var plan = EmitterPlanFixture.Create();
        var twin = plan.Models.OfType<EnumModelPlan>().First() with { Name = "Pipeline" };
        var withTwin = (plan with { Models = [.. plan.Models, twin] }).WithSingleSlice();

        var exception = await Assert.That(() => SourceEmitter.Emit(withTwin)).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("hand-written internal type 'Pipeline'");
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Preserve_All_Bound_Union_And_Registry_Evidence_Through_Source_Generation_And_Compilation()
    {
        var plan = pinned.Plan;
        var sources = SourceEmitter.Emit(plan);

        foreach (var union in plan.Unions)
        {
            var source = sources.Single(candidate => candidate.RelativePath.EndsWith(
                $"/Internal/{union.ConceptName}JsonConverter.cs", StringComparison.Ordinal));
            var expected = union
                .Variants
                .OrderBy(static variant => variant.Tag, StringComparer.Ordinal)
                .Select(static variant => new KeyValuePair<string, string>(variant.Tag, variant.TypeName));
            await Assert.That(ReadConverterMappings(source)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"converter {union.ConceptName}");
        }

        foreach (var structural in plan.Models.OfType<StructuralUnionModelPlan>())
        {
            var source = sources.Single(candidate => candidate.RelativePath.EndsWith(
                $"/Internal/{structural.Name}JsonConverter.cs", StringComparison.Ordinal));
            var expected = structural.Arms.Where(static arm => arm.Claim is null).SelectMany(arm => arm.Tokens.Select(token =>
                new KeyValuePair<string, string>(token.ToString(), arm.Name)));
            await Assert.That(ReadStructuralConverterMappings(source)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"converter {structural.Name}");
            var claimed = structural.Arms.Where(static arm => arm.Claim is not null).Select(static arm => arm.Name);
            await Assert.That(ReadFirstMatchOrder(source)).IsEquivalentTo(claimed, StringComparer.Ordinal, CollectionOrdering.Matching).Because($"converter {structural.Name}");
        }

        var requiredRegistryTypes = plan
            .Unions
            .SelectMany(static union => union
                .Variants.Select(static variant => variant.TypeName)
                .Append(union.Name)
                .Append(union.UnknownTypeName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var requiredStructuralRegistryTypes = plan
            .Models.OfType<StructuralUnionModelPlan>()
            .SelectMany(static model => model
                .Arms
                .Where(static arm => arm.Type.IsCollection)
                .Select(static arm => TypeReferenceNamePolicy.Format(arm.Type))
                .Append(model.Name));
        var allRequiredRegistryTypes = requiredRegistryTypes
            .Concat(requiredStructuralRegistryTypes)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await Assert.That(allRequiredRegistryTypes.Except(plan.Registry.TypeNames, StringComparer.Ordinal)).IsEmpty();

        // A bare container payload (list/dictionary not carried by an envelope DTO) joins the
        // registry from RegistryPlan.PayloadEntries rather than TypeNames, deduplicated by its
        // accessor name exactly as RegistryEmitter itself dedupes before emitting.
        var expectedPayloadEntryTypes = plan
            .Registry.PayloadEntries
            .Select(entry => (Entry: entry, Accessor: SerializerTypeNamePolicy.ContextPropertyName(entry)))
            .DistinctBy(static item => item.Accessor, StringComparer.Ordinal)
            .Select(static item => TypeSyntaxEmitter.Emit(item.Entry).ToString());
        var expectedRegistryTypes = plan
            .Registry.TypeNames
            .Concat(expectedPayloadEntryTypes)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var registrySource = sources.Single(static source =>
            source.RelativePath == "Internal/Serialization/OpenCodeJsonContext.cs");
        var emittedRegistryTypes = ReadRegistryTypes(registrySource);
        await Assert.That(emittedRegistryTypes).IsEquivalentTo(expectedRegistryTypes, StringComparer.Ordinal, CollectionOrdering.Matching).Because($"registry {registrySource.RelativePath}");
        await Assert.That(allRequiredRegistryTypes.Except(emittedRegistryTypes, StringComparer.Ordinal)).IsEmpty();

        var diagnostics = await GeneratedSourceCompiler.CompileWithSdkCoreAsync(sources);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task Emit_Should_Refuse_A_Stream_Payload_Union_With_Impossible_Tags()
    {
        var plan = await EmitterPlanFixture.CreateStreamPlanAsync();
        var client = plan.Clients.Single();
        var operation = client.Operations.Single();
        var stream = operation.Stream ?? throw new InvalidOperationException("The fixture must bind a stream.");
        var impossiblePayload = plan with
        {
            Clients = [client with { Operations = [operation with { Stream = stream with { PayloadTypeName = "IStreamFailureCause" } }] }],
        };

        var exception = Assert.Throws<InvalidOperationException>(() => SpecBinder.ResolveEmissionData(impossiblePayload));

        await Assert.That(exception.Message).IsEqualTo("Stream payload union 'IStreamFailureCause' declares impossible tags: Fail.");
    }

    [Test]
    public async Task Emit_Should_Resolve_Stream_Fallback_Metadata_From_Bound_Unions()
    {
        var plan = pinned.Plan;
        var streams = plan.Clients.SelectMany(static client => client.Operations).Select(static operation => operation.Stream)
            .OfType<StreamPlan>().ToDictionary(static stream => stream.AdapterTypeName, static stream => stream.UnknownPayloadTypeName, StringComparer.Ordinal);
        await Assert.That(streams["EventSubscribeResponseStreamAdapter"]).IsEqualTo("UnknownEvent");
        await Assert.That(streams["SessionLogResponseStreamAdapter"]).IsEqualTo("UnknownSessionLogItem");
        await Assert.That(plan.Unions.Where(static union => union.IsStreamFallbackCarrier).Select(static union => union.Name).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["IEvent", "ISessionLogItem"], StringComparer.Ordinal, CollectionOrdering.Matching);
        var client = plan.Clients.Single(static client => client.Name == "EventsClient");
        var operation = client.Operations.Single();
        var swapped = ResolveStreamOperation(plan, client, operation with { Stream = operation.Stream! with { PayloadTypeName = "SessionCreated" } });
        await Assert.That(swapped.Clients.Single().Operations.Single().Stream!.UnknownPayloadTypeName).IsNull();
        await Assert.That(swapped.Unions.Single(static union => union.Name == "IEvent").IsStreamFallbackCarrier).IsFalse();
        var renamed = ResolveStreamOperation(plan, client, operation with { OperationId = "renamed.stream" });
        await Assert.That(renamed.Clients.Single().Operations.Single().Stream!.UnknownPayloadTypeName).IsEqualTo("UnknownEvent");
        await Assert.That(renamed.Unions.Single(static union => union.Name == "IEvent").IsStreamFallbackCarrier).IsTrue();
    }

    private static EmitPlan ResolveStreamOperation(EmitPlan plan, ClientPlan client, OperationPlan operation) =>
        SpecBinder.ResolveEmissionData(plan with { Clients = [client with { Operations = [operation] }] });

    /// <summary>
    /// Guards <see cref="GeneratedSourceCompiler.GeneratedSurfaceConsumers"/>'s skip logic: the
    /// pinned plan must actually emit every entry's <c>RequiredEmission</c> twin, so a renamed or
    /// dropped raw client fails this assertion loudly instead of silently dropping its
    /// hand-written consumer out of <see cref="GeneratedSourceCompiler.CompileWithSdkCoreAsync"/>'s
    /// coverage.
    /// </summary>
    [Test]
    public async Task Emit_Should_Produce_Every_GeneratedSurfaceConsumers_RequiredEmission()
    {
        var plan = pinned.Plan;
        var sources = SourceEmitter.Emit(plan);
        var emittedPaths = sources.Select(static source => source.RelativePath).ToHashSet(StringComparer.Ordinal);

        foreach (var consumer in GeneratedSourceCompiler.GeneratedSurfaceConsumers)
        {
            await Assert.That(emittedPaths).Contains(consumer.RequiredEmission);
        }
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Deserialize_A_Shared_Pinned_Leaf_Through_Both_Stream_Interfaces()
    {
        var plan = pinned.Plan;
        var sources = SourceEmitter.Emit(plan);
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var durableType = assembly.GetType("OpenCode.Sdk.Models.ISessionEventDurable", throwOnError: true)!;
        var liveType = assembly.GetType("OpenCode.Sdk.Models.IEvent", throwOnError: true)!;
        var leafType = assembly.GetType("OpenCode.Sdk.Models.SessionCreated", throwOnError: true)!;
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));
        var payload = new FixtureLoader().Load("Serialization.shared-live-durable-event.json");

        var durable = JsonSerializer.Deserialize(payload, context.GetTypeInfo(durableType)
                                                          ?? throw new InvalidOperationException(
                                                              "Generated JSON context has no durable-event metadata."));
        var live = JsonSerializer.Deserialize(payload, context.GetTypeInfo(liveType)
                                                       ?? throw new InvalidOperationException("Generated JSON context has no live-event metadata."));

        await Assert.That(durable).IsNotNull();
        await Assert.That(live).IsNotNull();
        await Assert.That(durable.GetType()).IsEqualTo(leafType);
        await Assert.That(live.GetType()).IsEqualTo(leafType);
        await Assert.That(durableType.IsInstanceOfType(live)).IsTrue();
        await Assert.That(liveType.IsInstanceOfType(durable)).IsTrue();
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Preserve_An_Unclaimed_Pinned_Structural_Token_As_Unknown()
    {
        var plan = pinned.Plan;
        var sources = SourceEmitter.Emit(plan);
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(sources);
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.OpenCodeJsonContext", throwOnError: true)!;
        var context = (JsonSerializerContext)(contextType.GetProperty("Default")?.GetValue(null)
                                              ?? throw new InvalidOperationException("Generated JSON context has no Default instance."));

        var conditionType = assembly.GetType("OpenCode.Sdk.Models.FormWhenValue", throwOnError: true)!;
        var condition = JsonSerializer.Deserialize(
                            new FixtureLoader().Load("Serialization.structural-string-list.json"),
                            context.GetTypeInfo(conditionType)
                            ?? throw new InvalidOperationException("Generated JSON context has no form-condition metadata."))
                        ?? throw new InvalidOperationException("Form condition materialized null.");
        await Assert.That(conditionType.GetProperty("Kind")!.GetValue(condition)!.ToString()).IsEqualTo("Unknown");
        await Assert
            .That(((JsonElement)conditionType.GetProperty("Unknown")!.GetValue(condition)!).ValueKind)
            .IsEqualTo(JsonValueKind.Array);
    }

    [Test]
    public async Task Emit_Should_Not_Emit_Retired_Materialization_Helpers()
    {
        var sources = SourceEmitter.Emit(EmitterPlanFixture.Create());

        await Assert
            .That(sources.Select(static source => source.RelativePath.Split('/')[^1]))
            .DoesNotContain("OptionalCollectionInput.cs");
        var content = EmitterSnapshot.Create(sources);
        await Assert.That(content).DoesNotContain("WireNullRejecting");
        await Assert.That(content).DoesNotContain("NullElementRejectingListJsonConverter");
        await Assert.That(content).DoesNotContain("ListPayloadInput");
        await Assert.That(content).DoesNotContain("NonEmptyNoContentFailure");
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ReadConverterMappings(GeneratedSource source)
    {
        var root = Parse(source);
        return
        [
            .. root
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(static assignment =>
                    assignment.Left is ImplicitElementAccessSyntax && assignment.Right is TypeOfExpressionSyntax)
                .Select(static assignment => new KeyValuePair<string, string>(
                    ((LiteralExpressionSyntax)((ImplicitElementAccessSyntax)assignment.Left)
                        .ArgumentList.Arguments.Single()
                        .Expression).Token.ValueText,
                    ((TypeOfExpressionSyntax)assignment.Right).Type.ToString()))
                .OrderBy(static mapping => mapping.Key, StringComparer.Ordinal),
        ];
    }

    [Test]
    public async Task Emit_Should_Refuse_Two_Optional_Instantiations_That_Share_A_Converter_Name()
    {
        var model = Record("WidgetPatchRequest",
            Property("primitive", Named("double", isNullable: true), isRequired: false, emitsOptionalWrapper: true),
            Property("nominal", Named("Double", isNullable: true), isRequired: false, emitsOptionalWrapper: true));
        var plan = (EmitterPlanFixture.CreateModelSnapshot() with { Models = [model], Unions = [], Clients = [] }).WithSingleSlice();

        var exception = Assert.Throws<InvalidOperationException>(() => SourceEmitter.Emit(plan));

        await Assert.That(exception.Message).IsEqualTo("Multiple emitters produced 'Internal/Serialization/OptionalOfDoubleJsonConverter.cs'.");
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ReadStructuralConverterMappings(GeneratedSource source)
    {
        var root = Parse(source);
        return
        [
            .. root
                .DescendantNodes()
                .OfType<SwitchExpressionArmSyntax>()
                .Where(static arm => arm is
                {
                    Pattern: ConstantPatternSyntax { Expression: MemberAccessExpressionSyntax },
                    Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax },
                })
                .Select(static arm => new
                {
                    Token = ((MemberAccessExpressionSyntax)((ConstantPatternSyntax)arm.Pattern).Expression).Name.Identifier.ValueText,
                    Factory = ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)arm.Expression).Expression).Name.Identifier.ValueText,
                })
                .Where(static mapping => mapping.Factory.StartsWith("From", StringComparison.Ordinal))
                .Select(static mapping => new KeyValuePair<string, string>(mapping.Token, mapping.Factory[4..])),
        ];
    }

    /// <summary>The arms the first-match read tries, in the order it tries them; empty without one.</summary>
    private static IReadOnlyList<string> ReadFirstMatchOrder(GeneratedSource source)
    {
        var root = Parse(source);
        return
        [
            .. root
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Identifier.ValueText == "ReadObject")
                .SelectMany(static method => method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Select(static invocation => invocation.Expression)
                .OfType<MemberAccessExpressionSyntax>()
                .Select(static access => access.Name.Identifier.ValueText)
                .Where(static name => name.StartsWith("From", StringComparison.Ordinal) && name != "FromUnknown")
                .Select(static name => name[4..]),
        ];
    }

    private static IReadOnlyList<string> ReadRegistryTypes(GeneratedSource source)
    {
        var root = Parse(source);
        return
        [
            .. root
                .DescendantNodes()
                .OfType<AttributeSyntax>()
                .Where(static attribute => attribute.Name.ToString() == "JsonSerializable")
                .Select(static attribute =>
                    // The lone positional argument is always the typeof(...) target; a bare
                    // container payload's registration also carries a named
                    // TypeInfoPropertyName argument, so this reads the first argument
                    // specifically rather than demanding exactly one.
                    ((TypeOfExpressionSyntax)attribute.ArgumentList!.Arguments[0].Expression).Type.ToString())
                .Order(StringComparer.Ordinal),
        ];
    }

    private static CompilationUnitSyntax Parse(GeneratedSource source) =>
        CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(source.Utf8Source.Span)).GetCompilationUnitRoot();
}
