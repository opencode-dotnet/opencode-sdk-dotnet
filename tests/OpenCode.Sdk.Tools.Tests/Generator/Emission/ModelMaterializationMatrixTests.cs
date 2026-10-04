using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ModelMaterializationMatrixTests
{
    [Test]
    public async Task Bind_Should_Map_Required_And_Nullable_Axes_Independently()
    {
        var plan = await CreatePlanAsync();
        var model = plan.Models.OfType<ObjectModelPlan>()
            .Single(static candidate => candidate.Name == "MaterializationMatrix");

        foreach (var expected in new[]
                 {
                     ("requiredScalar", true, false),
                     ("requiredNullableScalar", true, true),
                     ("optionalScalar", false, true),
                     ("optionalNullableScalar", false, true),
                     ("requiredNumber", true, false),
                     ("requiredNullableNumber", true, true),
                     ("optionalNumber", false, true),
                     ("optionalNullableNumber", false, true),
                     ("requiredList", true, false),
                     ("requiredNullableList", true, true),
                     ("optionalList", false, true),
                     ("optionalNullableList", false, true),
                     ("requiredDictionary", true, false),
                     ("requiredNullableDictionary", true, true),
                     ("optionalDictionary", false, true),
                     ("optionalNullableDictionary", false, true),
                     ("openKnown", true, false),
                     ("requiredNullableAny", true, false),
                     ("optionalAny", false, true),
                 })
        {
            var property = model.Properties.Single(property => property.WireName == expected.Item1);
            await Assert.That(property.IsRequired).IsEqualTo(expected.Item2);
            await Assert.That(property.Type.IsNullable).IsEqualTo(expected.Item3);
        }

        var nonnullItems = (ListTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "nonnullItems").Type;
        var nullableItems = (ListTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "nullableItems").Type;
        var nonnullValues = (DictionaryTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "nonnullValues").Type;
        var nullableValues = (DictionaryTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "nullableValues").Type;
        var anyItems = (ListTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "anyItems").Type;
        var nullableAnyItems = (ListTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "nullableAnyItems").Type;
        var anyValues = (DictionaryTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "anyValues").Type;
        var nullableAnyValues = (DictionaryTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "nullableAnyValues").Type;
        var optionalAny = (NamedTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "optionalAny").Type;
        await Assert.That(nonnullItems.ElementType.IsNullable).IsFalse();
        await Assert.That(nullableItems.ElementType.IsNullable).IsTrue();
        await Assert.That(nonnullValues.ValueType.IsNullable).IsFalse();
        await Assert.That(nullableValues.ValueType.IsNullable).IsTrue();
        await Assert.That(anyItems.ElementType.IsNullable).IsFalse();
        await Assert.That(anyItems.ElementType.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
        await Assert.That(nullableAnyItems.ElementType.IsNullable).IsFalse();
        await Assert.That(nullableAnyItems.ElementType.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
        await Assert.That(anyValues.ValueType.IsNullable).IsFalse();
        await Assert.That(anyValues.ValueType.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
        await Assert.That(nullableAnyValues.ValueType.IsNullable).IsFalse();
        await Assert.That(nullableAnyValues.ValueType.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
        await Assert.That(optionalAny.IsNullable).IsTrue();
        await Assert.That(optionalAny.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
        var requiredAny = (NamedTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "requiredAny").Type;
        var requiredNullableAny = (NamedTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "requiredNullableAny").Type;
        var freeform = (DictionaryTypeReferencePlan)model.Properties
            .Single(static property => property.WireName == "freeform").Type;
        await Assert.That(requiredAny.Name).IsEqualTo("JsonElement");
        await Assert.That(requiredAny.IsNullable).IsFalse();
        await Assert.That(requiredAny.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
        await Assert.That(requiredNullableAny.IsNullable).IsFalse();
        await Assert.That(requiredNullableAny.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
        await Assert.That(freeform.ValueType.IsNullable).IsFalse();
        await Assert.That(freeform.ValueType.JsonNullRepresentation).IsEqualTo(JsonNullRepresentation.InBand);
    }

    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Compile_Source_Generate_And_Materialize_The_Nullability_Matrix()
    {
        var plan = await CreatePlanAsync();
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));
        var typeInfo = ResolveTypeInfo(assembly, "OpenCode.Sdk.Models.MaterializationMatrix");

        var value = Deserialize(CreatePayload(), typeInfo);
        await AssertOptionalPropertiesAreNullAsync(value);
        await AssertCollectionChildrenAndLiteralAsync(value);
        await AssertUnknownFieldsAreSkippedAsync(value);
        await AssertSerializationAsync(value, typeInfo);
        await AssertExplicitOptionalNullsAsync(typeInfo);
        await AssertNonNullOptionalAndNullableValuesAsync(typeInfo);
        await AssertRequiredFailuresAsync(typeInfo);
        await AssertTristateRequestMembersAsync(assembly, plan);
        await AssertRootUnionNullFailsAsync(assembly, plan);
    }

    /// <summary>
    /// The request side is the exact opposite of <see cref="AssertExplicitOptionalNullsAsync" />:
    /// where a response-side optional member collapses an explicit null to absence, a member of a
    /// schema a request body reaches keeps the two apart. The optional member the document does
    /// not admit null for keeps the old behaviour, which is what proves the key is the schema and
    /// not the direction alone.
    /// </summary>
    private static async Task AssertTristateRequestMembersAsync(Assembly assembly, EmitPlan plan)
    {
        var patch = plan
            .Models.OfType<ObjectModelPlan>()
            .Single(static model => model.Properties.Any(static property => property.WireName == "patchScalar"));
        await Assert.That(PatchMember(patch, "patchScalar").EmitsOptionalWrapper).IsTrue();
        await Assert.That(PatchMember(patch, "patchNumber").EmitsOptionalWrapper).IsTrue();
        await Assert.That(PatchMember(patch, "patchList").EmitsOptionalWrapper).IsTrue();
        await Assert.That(PatchMember(patch, "patchDictionary").EmitsOptionalWrapper).IsTrue();
        await Assert.That(PatchMember(patch, "patchPlain").EmitsOptionalWrapper).IsFalse();
        await Assert.That(PatchMember(patch, "patchRequired").EmitsOptionalWrapper).IsFalse();

        var typeInfo = ResolveTypeInfo(assembly, $"OpenCode.Sdk.Models.{patch.Name}");
        var absent = Deserialize(new JsonObject { ["patchRequired"] = null, }, typeInfo);
        await Assert.That(await SerializeAsync(absent, typeInfo)).IsEqualTo("""{"patchRequired":null}""");

        var cleared = Deserialize(
            new JsonObject
            {
                ["patchScalar"] = null,
                ["patchNumber"] = null,
                ["patchList"] = null,
                ["patchDictionary"] = null,
                ["patchPlain"] = null,
                ["patchRequired"] = null,
            },
            typeInfo);
        using (var document = JsonDocument.Parse(await SerializeAsync(cleared, typeInfo)))
        {
            foreach (var name in new[] { "patchScalar", "patchNumber", "patchList", "patchDictionary", })
            {
                await Assert.That(document.RootElement.GetProperty(name).ValueKind).IsEqualTo(JsonValueKind.Null);
            }

            await Assert.That(document.RootElement.TryGetProperty("patchPlain", out _)).IsFalse();
        }

        var set = Deserialize(
            new JsonObject
            {
                ["patchScalar"] = "set",
                ["patchNumber"] = 4.5,
                ["patchList"] = new JsonArray(JsonValue.Create("set")),
                ["patchDictionary"] = new JsonObject { ["key"] = "set", },
                ["patchPlain"] = "plain",
                ["patchRequired"] = "kept",
            },
            typeInfo);
        using (var document = JsonDocument.Parse(await SerializeAsync(set, typeInfo)))
        {
            await Assert.That(document.RootElement.GetProperty("patchScalar").GetString()).IsEqualTo("set");
            await Assert.That(document.RootElement.GetProperty("patchNumber").GetDouble()).IsEqualTo(4.5);
            await Assert.That(document.RootElement.GetProperty("patchList")[0].GetString()).IsEqualTo("set");
            await Assert.That(document.RootElement.GetProperty("patchDictionary").GetProperty("key").GetString()).IsEqualTo("set");
            await Assert.That(document.RootElement.GetProperty("patchPlain").GetString()).IsEqualTo("plain");
            await Assert.That(document.RootElement.GetProperty("patchRequired").GetString()).IsEqualTo("kept");
        }
    }

    private static ModelPropertyPlan PatchMember(ObjectModelPlan model, string wireName) =>
        model.Properties.Single(property => string.Equals(property.WireName, wireName, StringComparison.Ordinal));

    private static async Task AssertOptionalPropertiesAreNullAsync(object value)
    {
        foreach (var name in new[]
                 {
                     "OptionalScalar",
                     "OptionalNullableScalar",
                     "OptionalNumber",
                     "OptionalNullableNumber",
                     "OptionalList",
                     "OptionalNullableList",
                     "OptionalDictionary",
                     "OptionalNullableDictionary",
                     "OptionalAny",
                 })
        {
            await Assert.That(GetProperty(value, name)).IsNull();
        }
    }

    private static async Task AssertCollectionChildrenAndLiteralAsync(object value)
    {
        await Assert.That(((IList)GetProperty(value, "NonnullItems")!)[0]).IsNull();
        await Assert.That(((IList)GetProperty(value, "NullableItems")!)[0]).IsNull();
        await Assert.That(((IDictionary)GetProperty(value, "NonnullValues")!)["key"]).IsNull();
        await Assert.That(((IDictionary)GetProperty(value, "NullableValues")!)["key"]).IsNull();
        await Assert.That(((IList)GetProperty(value, "Choices")!)[0]).IsNull();
        var requiredAny = (JsonElement)GetProperty(value, "RequiredAny")!;
        var requiredNullableAny = (JsonElement)GetProperty(value, "RequiredNullableAny")!;
        var anyItem = (JsonElement)((IList)GetProperty(value, "AnyItems")!)[0]!;
        var nullableAnyItem = (JsonElement)((IList)GetProperty(value, "NullableAnyItems")!)[0]!;
        var anyValue = (JsonElement)((IDictionary)GetProperty(value, "AnyValues")!)["key"]!;
        var nullableAnyValue = (JsonElement)((IDictionary)GetProperty(value, "NullableAnyValues")!)["key"]!;
        var freeformValue = (JsonElement)((IDictionary)GetProperty(value, "Freeform")!)["key"]!;
        await Assert.That(requiredAny.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(requiredNullableAny.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(anyItem.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(nullableAnyItem.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(anyValue.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(nullableAnyValue.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(freeformValue.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That((bool)GetProperty(value, "FixedFlag")!).IsFalse();
    }

    private static async Task AssertUnknownFieldsAreSkippedAsync(object value)
    {
        await Assert.That(GetProperty(value, "RequiredScalar")).IsEqualTo("value");
        var openKnown = GetProperty(value, "OpenKnown")
                        ?? throw new InvalidOperationException("The open known object deserialized to null.");
        await Assert.That(GetProperty(openKnown, "Value")).IsEqualTo("known");
    }

    private static async Task AssertSerializationAsync(object value, JsonTypeInfo typeInfo)
    {
        var serialized = await SerializeAsync(value, typeInfo);
        using (var document = JsonDocument.Parse(serialized))
        {
            foreach (var name in new[]
                     {
                         "requiredNullableScalar",
                         "requiredNullableNumber",
                         "requiredNullableList",
                         "requiredNullableDictionary",
                         "requiredAny",
                         "requiredNullableAny",
                     })
            {
                await Assert.That(document.RootElement.GetProperty(name).ValueKind).IsEqualTo(JsonValueKind.Null);
            }

            await Assert.That(document.RootElement.TryGetProperty("optionalScalar", out _)).IsFalse();
            await Assert.That(document.RootElement.TryGetProperty("optionalList", out _)).IsFalse();
            await Assert.That(document.RootElement.TryGetProperty("optionalDictionary", out _)).IsFalse();
            await Assert.That(document.RootElement.TryGetProperty("optionalAny", out _)).IsFalse();
            await Assert.That(document.RootElement.GetProperty("anyItems")[0].ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(document.RootElement.GetProperty("nullableAnyItems")[0].ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(document.RootElement.GetProperty("anyValues").GetProperty("key").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
            await Assert.That(document.RootElement.GetProperty("nullableAnyValues").GetProperty("key").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
            await Assert.That(document.RootElement.GetProperty("freeform").GetProperty("key").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
            await Assert.That(document.RootElement.GetProperty("fixedFlag").GetBoolean()).IsFalse();
        }
    }

    private static async Task AssertExplicitOptionalNullsAsync(JsonTypeInfo typeInfo)
    {
        var explicitOptionalNulls = CreatePayload();
        foreach (var name in new[]
                 {
                     "optionalScalar",
                     "optionalNullableScalar",
                     "optionalNumber",
                     "optionalNullableNumber",
                     "optionalList",
                     "optionalNullableList",
                     "optionalDictionary",
                     "optionalNullableDictionary",
                     "optionalAny",
                 })
        {
            explicitOptionalNulls[name] = null;
        }

        var explicitNullValue = Deserialize(explicitOptionalNulls, typeInfo);
        var explicitNullJson = await SerializeAsync(explicitNullValue, typeInfo);
        using (var document = JsonDocument.Parse(explicitNullJson))
        {
            await Assert.That(document.RootElement.TryGetProperty("optionalNullableScalar", out _)).IsFalse();
            await Assert.That(document.RootElement.TryGetProperty("optionalNullableNumber", out _)).IsFalse();
            await Assert.That(document.RootElement.TryGetProperty("optionalNullableList", out _)).IsFalse();
            await Assert.That(document.RootElement.TryGetProperty("optionalNullableDictionary", out _)).IsFalse();
            await Assert.That(document.RootElement.TryGetProperty("optionalAny", out _)).IsFalse();
        }
    }

    private static async Task AssertNonNullOptionalAndNullableValuesAsync(JsonTypeInfo typeInfo)
    {
        var payload = CreatePayload();
        payload["requiredNullableScalar"] = "required";
        payload["requiredNullableNumber"] = 1.5;
        payload["requiredNullableList"] = new JsonArray(JsonValue.Create("required"));
        payload["requiredNullableDictionary"] = new JsonObject { ["key"] = "required", };
        payload["optionalScalar"] = "optional";
        payload["optionalNullableScalar"] = "optional-nullable";
        payload["optionalNumber"] = 2.5;
        payload["optionalNullableNumber"] = 3.5;
        payload["optionalList"] = new JsonArray(JsonValue.Create("optional"));
        payload["optionalNullableList"] = new JsonArray(JsonValue.Create("optional-nullable"));
        payload["optionalDictionary"] = new JsonObject { ["key"] = "optional", };
        payload["optionalNullableDictionary"] = new JsonObject { ["key"] = "optional-nullable", };
        payload["optionalAny"] = new JsonObject { ["present"] = true, };

        var value = Deserialize(payload, typeInfo);
        var serialized = await SerializeAsync(value, typeInfo);
        using var document = JsonDocument.Parse(serialized);
        foreach (var name in new[]
                 {
                     "requiredNullableScalar",
                     "requiredNullableNumber",
                     "requiredNullableList",
                     "requiredNullableDictionary",
                     "optionalScalar",
                     "optionalNullableScalar",
                     "optionalNumber",
                     "optionalNullableNumber",
                     "optionalList",
                     "optionalNullableList",
                     "optionalDictionary",
                     "optionalNullableDictionary",
                     "optionalAny",
                 })
        {
            await Assert.That(document.RootElement.TryGetProperty(name, out _)).IsTrue();
        }

        await Assert.That(document.RootElement.GetProperty("requiredNullableScalar").GetString()).IsEqualTo("required");
        await Assert.That(document.RootElement.GetProperty("requiredNullableNumber").GetDouble()).IsEqualTo(1.5);
        await Assert.That(document.RootElement.GetProperty("requiredNullableList")[0].GetString()).IsEqualTo("required");
        await Assert.That(document.RootElement.GetProperty("requiredNullableDictionary").GetProperty("key").GetString())
            .IsEqualTo("required");
        await Assert.That(document.RootElement.GetProperty("optionalNumber").GetDouble()).IsEqualTo(2.5);
        await Assert.That(document.RootElement.GetProperty("optionalNullableNumber").GetDouble()).IsEqualTo(3.5);
    }

    private static async Task AssertRequiredFailuresAsync(JsonTypeInfo typeInfo)
    {
        foreach (var name in new[]
                 {
                     "requiredScalar",
                     "requiredNullableScalar",
                     "requiredNumber",
                     "requiredNullableNumber",
                     "requiredList",
                     "requiredNullableList",
                     "requiredDictionary",
                     "requiredNullableDictionary",
                     "requiredChoice",
                     "choices",
                     "fixedFlag",
                     "requiredAny",
                     "requiredNullableAny",
                     "anyItems",
                     "nullableAnyItems",
                     "anyValues",
                     "nullableAnyValues",
                     "freeform",
                     "openKnown",
                 })
        {
            var missing = CreatePayload();
            _ = missing.Remove(name);
            _ = await Assert.That(() => JsonSerializer.Deserialize(missing.ToJsonString(), typeInfo)).Throws<JsonException>();
        }

        foreach (var name in new[]
                 {
                     "requiredScalar",
                     "requiredNumber",
                     "requiredList",
                     "requiredDictionary",
                     "requiredChoice",
                     "choices",
                     "fixedFlag",
                     "anyItems",
                     "nullableAnyItems",
                     "anyValues",
                     "nullableAnyValues",
                     "freeform",
                     "openKnown",
                 })
        {
            var explicitNull = CreatePayload();
            explicitNull[name] = null;
            _ = await Assert.That(() => JsonSerializer.Deserialize(explicitNull.ToJsonString(), typeInfo)).Throws<JsonException>();
        }
    }

    private static async Task<EmitPlan> CreatePlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(new MaterializationMatrixScenario());
        return new BindingTestHost().Bind(
            document,
            Selection(
                MaterializationMatrixScenario.GetOperationId,
                MaterializationMatrixScenario.ChoiceOperationId,
                MaterializationMatrixScenario.PatchOperationId),
            Curation(
                Groups("matrix", RootGroup()),
                operationNames: [OperationName(MaterializationMatrixScenario.PatchOperationId, "PatchMatrixAsync")]));
    }

    private static JsonObject CreatePayload() => new()
    {
        ["requiredScalar"] = "value",
        ["requiredNullableScalar"] = null,
        ["requiredNumber"] = 1.0,
        ["requiredNullableNumber"] = null,
        ["requiredList"] = new JsonArray(JsonValue.Create("value")),
        ["requiredNullableList"] = null,
        ["requiredDictionary"] = new JsonObject { ["key"] = "value", },
        ["requiredNullableDictionary"] = null,
        ["nonnullItems"] = new JsonArray((JsonNode?)null),
        ["nullableItems"] = new JsonArray((JsonNode?)null),
        ["nonnullValues"] = new JsonObject { ["key"] = null, },
        ["nullableValues"] = new JsonObject { ["key"] = null, },
        ["requiredAny"] = null,
        ["requiredNullableAny"] = null,
        ["anyItems"] = new JsonArray((JsonNode?)null),
        ["nullableAnyItems"] = new JsonArray((JsonNode?)null),
        ["anyValues"] = new JsonObject { ["key"] = null, },
        ["nullableAnyValues"] = new JsonObject { ["key"] = null, },
        ["freeform"] = new JsonObject { ["key"] = null, },
        ["openKnown"] = new JsonObject { ["value"] = "known", ["unexpectedOpen"] = true, },
        ["requiredChoice"] = new JsonObject { ["type"] = "alpha", },
        ["choices"] = new JsonArray((JsonNode?)null),
        ["fixedFlag"] = false,
        ["unexpectedClosed"] = true,
    };

    private static object Deserialize(JsonObject payload, JsonTypeInfo typeInfo) =>
        JsonSerializer.Deserialize(payload.ToJsonString(), typeInfo)
        ?? throw new InvalidOperationException("The matrix payload deserialized to null.");

    private static JsonTypeInfo ResolveTypeInfo(Assembly assembly, string typeName)
    {
        var contextType = assembly.GetType("OpenCode.Sdk.Internal.Serialization.OpenCodeJsonContext", throwOnError: true)!;
        var context = contextType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as JsonSerializerContext
                      ?? throw new InvalidOperationException("The generated JSON context has no Default instance.");
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return context.GetTypeInfo(type)
               ?? throw new InvalidOperationException($"The generated JSON context has no metadata for '{typeName}'.");
    }

    private static object? GetProperty(object value, string name)
    {
        var property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                       ?? throw new InvalidOperationException($"The matrix type has no '{name}' property.");
        return property.GetValue(value);
    }

    private static async Task<string> SerializeAsync(object value, JsonTypeInfo typeInfo)
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, value, typeInfo);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task AssertRootUnionNullFailsAsync(Assembly assembly, EmitPlan plan)
    {
        var operation = plan.Clients.SelectMany(static client => client.Operations)
            .Single(static candidate => candidate.Envelope?.PayloadType is NamedTypeReferencePlan { Name: "IMatrixChoice" });
        var adapterType = assembly.GetType(
            $"OpenCode.Sdk.Internal.ResponseAdapters.{operation.Envelope!.AdapterTypeName}",
            throwOnError: true)!;
        var adapter = adapterType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                      ?? throw new InvalidOperationException("The generated response adapter has no Instance.");
        var adapt = adapterType.GetMethod("Adapt", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("The generated response adapter has no Adapt method.");

        var exception = Assert.Throws<TargetInvocationException>(() => _ = adapt.Invoke(adapter, [200, "null"]));

        await Assert.That(exception.InnerException?.GetType().FullName).IsEqualTo("OpenCode.Sdk.OpenCodeTransportException");
    }

    /// <summary>
    /// The open-object shape end to end: the named member materializes typed, every member the
    /// document leaves open lands in the extension-data bag under its wire name, and both sides
    /// serialize back, so nothing the server sent is lost.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Compile_And_Roundtrip_An_Open_Models_Extension_Data()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("OpenSettings", schema => schema
                .Type("object")
                .Property("timeout", property => property.Type("number"))
                .AllOf(rest => rest.Type("object").AdditionalProperties(value => value.Unrestricted())))
            .WithSchema("WidgetError", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("_tag", property => property.Type("string").Enum("WidgetError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithOperation("settings.get", path: "/api/settings", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("OpenSettings"))
                .Response(400, "application/json", schema => schema.Ref("WidgetError")))));
        var plan = new BindingTestHost().Bind(document, Selection("settings.get"),
            Curation(Groups("settings", RootGroup()), redactedOpenMembers: [OpenMembers("OpenSettings", redact: false)]));

        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));
        var typeInfo = ResolveTypeInfo(assembly, "OpenCode.Sdk.Models.OpenSettings");

        var value = Deserialize(
            new JsonObject
            {
                ["timeout"] = 2.5,
                ["baseURL"] = "https://example.test",
                ["nested"] = new JsonObject { ["enabled"] = true, },
            },
            typeInfo);
        await Assert.That(GetProperty(value, "Timeout")).IsEqualTo(2.5);
        var bag = (IReadOnlyDictionary<string, JsonElement>)(GetProperty(value, "AdditionalProperties")
                                                            ?? throw new InvalidOperationException("The open members materialized to null."));
        await Assert.That(bag.Keys).IsEquivalentTo(["baseURL", "nested"]);
        await Assert.That(bag["baseURL"].GetString()).IsEqualTo("https://example.test");
        await Assert.That(bag["nested"].GetProperty("enabled").GetBoolean()).IsTrue();
        // The serializer's writable bag is not part of the public surface.
        await Assert.That(value.GetType().GetProperty("OpenMembers", BindingFlags.Instance | BindingFlags.Public)).IsNull();

        using var document2 = JsonDocument.Parse(await SerializeAsync(value, typeInfo));
        await Assert.That(document2.RootElement.GetProperty("timeout").GetDouble()).IsEqualTo(2.5);
        await Assert.That(document2.RootElement.GetProperty("baseURL").GetString()).IsEqualTo("https://example.test");
        await Assert.That(document2.RootElement.GetProperty("nested").GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(document2.RootElement.TryGetProperty("openMembers", out _)).IsFalse();
        await Assert.That(document2.RootElement.TryGetProperty("additionalProperties", out _)).IsFalse();

        // A body without open members answers the shared empty view; the serializer creates the
        // internal bag only on the first open member, which the emitter test pins by shape (no
        // initializer on the bag).
        var closed = Deserialize(new JsonObject { ["timeout"] = 1.0, }, typeInfo);
        await Assert.That((IReadOnlyDictionary<string, JsonElement>)GetProperty(closed, "AdditionalProperties")!).IsEmpty();
        await Assert.That(await SerializeAsync(closed, typeInfo)).IsEqualTo("""{"timeout":1}""");
    }

    /// <summary>
    /// The four container envelope shapes Task 4 wires through <c>TypePlanBinder</c>: a
    /// Data-wrapped list, a Data-wrapped dictionary, a bare list, and a bare dictionary. One
    /// compile carries all four so the round trip pays the Roslyn cost once; each leg asserts
    /// the adapter materializes the typed payload, and the Data-shaped legs additionally
    /// assert their DTO wall still refuses a missing 'data'.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Compile_And_Roundtrip_Container_Envelope_Payloads()
    {
        var (plan, operations) = await CreateContainerEnvelopePlanAsync();
        await AssertContainerPayloadShapesAsync(operations);

        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));

        await AssertListPayloadRoundTripsAsync(assembly, operations.DataList, """{"data":[{"id":"a"}]}""");
        await AssertMapPayloadRoundTripsAsync(assembly, operations.DataMap, """{"data":{"k":{"id":"a"}}}""");
        await AssertListPayloadRoundTripsAsync(assembly, operations.BareList, """[{"id":"a"}]""");
        await AssertMapPayloadRoundTripsAsync(assembly, operations.BareMap, """{"k":{"id":"a"}}""");

        await AssertMissingDataFailsAsync(assembly, operations.DataList.Envelope!, "{}");
        await AssertMissingDataFailsAsync(assembly, operations.DataMap.Envelope!, "{}");
    }

    /// <summary>
    /// The promoted inline payload Task 5 admits: a bare success body that is an inline object
    /// becomes a model named from the operation, and a single-property wrapper is that model
    /// itself rather than the value it wraps.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Compile_And_Roundtrip_Promoted_Inline_Envelope_Payloads()
    {
        var (plan, stats, handoff) = await CreatePromotedInlinePlanAsync();
        await Assert.That(((NamedTypeReferencePlan)stats.Envelope!.PayloadType!).Name).IsEqualTo("WidgetStatsData");
        await Assert.That(((NamedTypeReferencePlan)handoff.Envelope!.PayloadType!).Name).IsEqualTo("WidgetHandoffData");

        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));

        var stitched = AdaptSuccess(assembly, stats.Envelope, """{"count":3}""");
        var payload = GetProperty(stitched, stats.Envelope.PayloadName!)
                      ?? throw new InvalidOperationException("The promoted payload materialized to null.");
        await Assert.That(GetProperty(payload, "Count")).IsEqualTo(3L);

        var wrapped = AdaptSuccess(assembly, handoff.Envelope, """{"handoff":{"id":"a"}}""");
        var wrapper = GetProperty(wrapped, handoff.Envelope.PayloadName!)
                      ?? throw new InvalidOperationException("The promoted wrapper materialized to null.");
        await Assert.That(GetProperty(GetProperty(wrapper, "Handoff")!, "Id")).IsEqualTo("a");
    }

    /// <summary>
    /// The single-key facet end to end: the emitted DTO reads the body under the operation's own
    /// key, the response carries the value directly under that key's name, and the
    /// represented-nullable arm still materializes as CLR null on a success.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Materialize_A_Single_Key_Envelope_Under_Its_Own_Wire_Key()
    {
        var (plan, operation) = await CreateSingleKeyEnvelopePlanAsync();
        await Assert.That(operation.Envelope!.PayloadName).IsEqualTo("Handoff");

        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));

        var present = AdaptSuccess(assembly, operation.Envelope, """{"handoff":{"id":"a"}}""");
        var payload = GetProperty(present, "Handoff")
                      ?? throw new InvalidOperationException("The single-key payload materialized to null.");
        await Assert.That(GetProperty(payload, "Id")).IsEqualTo("a");

        var absent = AdaptSuccess(assembly, operation.Envelope, """{"handoff":null}""");
        await Assert.That((bool)GetProperty(absent, "IsError")!).IsFalse();
        await Assert.That(GetProperty(absent, "Handoff")).IsNull();
    }

    /// <summary>
    /// Task 6's response-state guard: a nullable Data payload's success path lets a wire
    /// <c>null</c> flow through as CLR null while <c>IsError</c> stays
    /// false, a present value still round-trips, and the error path still throws the guard —
    /// distinct from the field-null coalesce the non-nullable shape keeps using.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Materialize_A_Nullable_Data_Envelope_Payload_By_Response_State()
    {
        var (plan, operation) = await CreateNullableDataEnvelopePlanAsync();
        await Assert.That(operation.Envelope!.PayloadType!.IsNullable).IsTrue();

        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));

        var present = AdaptSuccess(assembly, operation.Envelope, """{"data":{"id":"a"}}""");
        var presentPayload = GetProperty(present, operation.Envelope.PayloadName!)
                             ?? throw new InvalidOperationException("Expected a present payload.");
        await Assert.That(GetProperty(presentPayload, "Id")).IsEqualTo("a");

        var nullSuccess = AdaptSuccess(assembly, operation.Envelope, """{"data":null}""");
        await Assert.That((bool)GetProperty(nullSuccess, "IsError")!).IsFalse();
        await Assert.That(GetProperty(nullSuccess, operation.Envelope.PayloadName!)).IsNull();
        var nullSuccessText = nullSuccess.ToString()
                              ?? throw new InvalidOperationException("ToString returned null.");
        await Assert.That(nullSuccessText).Contains($"{operation.Envelope.PayloadName} = ");
        await Assert.That(nullSuccessText).DoesNotContain($"{operation.Envelope.PayloadName} = null");

        // A nullable payload accepts an explicit wire null, but the DTO's 'data' member stays
        // `required` — an absent 'data' key is still a materialization failure, not a second
        // spelling of null.
        await AssertMissingDataFailsAsync(assembly, operation.Envelope, "{}");

        var (adapter, adapt) = ResolveAdapter(assembly, operation.Envelope);
        var error = adapt.Invoke(adapter, [400, """{"_tag":"WidgetError","message":"bad"}"""])
                    ?? throw new InvalidOperationException("Adapt returned null for the error path.");
        await Assert.That((bool)GetProperty(error, "IsError")!).IsTrue();
        var exception = Assert.Throws<TargetInvocationException>(
            () => GetProperty(error, operation.Envelope.PayloadName!));
        await Assert.That(exception.InnerException).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception.InnerException!.Message).Contains("IsError");
    }

    /// <summary>
    /// The non-nullable wall next to the nullable path it sits beside: Task 6 only changes the
    /// guard shape a nullable-typed payload uses, so a non-nullable payload's wire null still
    /// fails materialization behind <c>RespectNullableAnnotations</c>, exactly as before.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Still_Fail_To_Materialize_A_Wire_Null_For_A_Non_Nullable_Data_Envelope_Payload()
    {
        var (plan, operations) = await CreateContainerEnvelopePlanAsync();
        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));

        await AssertMissingDataFailsAsync(assembly, operations.DataList.Envelope!, """{"data":null}""");
    }

    private static async Task<(EmitPlan Plan, OperationPlan Operation)> CreateSingleKeyEnvelopePlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetError", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("_tag", property => property.Type("string").Enum("WidgetError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithOperation("widget.handoff", path: "/api/widget/handoff", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .AdditionalPropertiesFalse()
                    .Property("handoff", property => property.AnyOf(
                        static branch => branch.Ref("WidgetInfo"),
                        static branch => branch.Type("null")), required: true))
                .Response(400, "application/json", schema => schema.Ref("WidgetError")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.handoff"),
            Curation(Groups("widget", RootGroup())));

        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();
        return (plan, operation);
    }

    private static async Task<(EmitPlan Plan, OperationPlan Operation)> CreateNullableDataEnvelopePlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetError", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("_tag", property => property.Type("string").Enum("WidgetError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithSchema("WidgetResponse", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property.AnyOf(
                    static branch => branch.Ref("WidgetInfo"),
                    static branch => branch.Type("null")), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetResponse"))
                .Response(400, "application/json", schema => schema.Ref("WidgetError")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.list"),
            Curation(Groups("widget", RootGroup())));

        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();
        return (plan, operation);
    }

    /// <summary>
    /// The addendum's array arm: a location envelope's array-of-inline-object <c>data</c>
    /// promotes its item under the operation-scoped name and round-trips both siblings.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Compile_And_Roundtrip_A_Promoted_Data_Location_List_Item()
    {
        var (plan, operation) = await CreatePromotedDataLocationListPlanAsync();
        await Assert.That(operation.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocationList);
        var elementType = (NamedTypeReferencePlan)((ListTypeReferencePlan)operation.Envelope.PayloadType!).ElementType;
        await Assert.That(elementType.Name).IsEqualTo("WidgetListData");

        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));

        var result = AdaptSuccess(assembly, operation.Envelope, """{"data":[{"id":"a"}],"location":{"directory":"widget-place"}}""");
        var items = (IList)GetProperty(result, operation.Envelope.PayloadName!)!;
        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(GetProperty(items[0]!, "Id")).IsEqualTo("a");
        var location = GetProperty(result, "Location")
                       ?? throw new InvalidOperationException("Expected a present location.");
        await Assert.That(GetProperty(location, "Directory")).IsEqualTo("widget-place");
    }

    private static async Task<(EmitPlan Plan, OperationPlan Operation)> CreatePromotedDataLocationListPlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("PlaceInfo", schema => schema
                .Type("object")
                .Property("directory", property => property.Type("string"), required: true))
            .WithSchema("WidgetError", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("_tag", property => property.Type("string").Enum("WidgetError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithSchema("WidgetEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("location", property => property.Ref("PlaceInfo"), required: true)
                .Property("data", property => property
                    .Type("array")
                    .Items(static item => item
                        .Type("object")
                        .Property("id", static inner => inner.Type("string"), required: true)), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetEnvelope"))
                .Response(400, "application/json", schema => schema.Ref("WidgetError")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.list"),
            Curation(Groups("widget", RootGroup())));

        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();
        return (plan, operation);
    }

    /// <summary>
    /// The mechanism extension: a location envelope's 'data' member is a RefNode naming an
    /// ARRAY component directly (vcs.branches' exact shape, Vcs.BranchList =
    /// {"type":"array","items":{"type":"string"}}) rather than wrapping the array inline. The
    /// item is a primitive string, which the type machinery binds through its ordinary
    /// RefNode -&gt; ArrayNode -&gt; primitive path once the guard stops refusing the ref; the DTO
    /// carries the payload, so no bare-container registry entry is needed.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Bind_Should_Compile_And_Roundtrip_A_Data_Location_List_Ref_To_A_Named_Array_Component()
    {
        var (plan, operation) = await CreateRefToNamedArrayDataLocationListPlanAsync();
        await Assert.That(operation.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocationList);
        var elementType = (NamedTypeReferencePlan)((ListTypeReferencePlan)operation.Envelope.PayloadType!).ElementType;
        await Assert.That(elementType.Name).IsEqualTo("string");
        await Assert.That(plan.Registry.PayloadEntries.Count).IsEqualTo(0);

        var assembly = await GeneratedSourceCompiler.CompileAndLoadWithSdkCoreAsync(SourceEmitter.Emit(plan));

        var result = AdaptSuccess(assembly, operation.Envelope, """{"data":["main","dev"],"location":{"directory":"widget-place"}}""");
        var items = (IList)GetProperty(result, operation.Envelope.PayloadName!)!;
        await Assert.That(items.Count).IsEqualTo(2);
        await Assert.That((string?)items[0]).IsEqualTo("main");
        await Assert.That((string?)items[1]).IsEqualTo("dev");
        var location = GetProperty(result, "Location")
                       ?? throw new InvalidOperationException("Expected a present location.");
        await Assert.That(GetProperty(location, "Directory")).IsEqualTo("widget-place");
    }

    private static async Task<(EmitPlan Plan, OperationPlan Operation)> CreateRefToNamedArrayDataLocationListPlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("PlaceInfo", schema => schema
                .Type("object")
                .Property("directory", property => property.Type("string"), required: true))
            .WithSchema("WidgetError", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("_tag", property => property.Type("string").Enum("WidgetError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithSchema("WidgetNameList", schema => schema
                .Type("array")
                .Items(static item => item.Type("string")))
            .WithSchema("WidgetEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("location", property => property.Ref("PlaceInfo"), required: true)
                .Property("data", property => property.Ref("WidgetNameList"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetEnvelope"))
                .Response(400, "application/json", schema => schema.Ref("WidgetError")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.list"),
            Curation(Groups("widget", RootGroup())));

        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();
        return (plan, operation);
    }

    private static async Task<(EmitPlan Plan, OperationPlan Stats, OperationPlan Handoff)> CreatePromotedInlinePlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetError", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("_tag", property => property.Type("string").Enum("WidgetError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            // Both bodies carry a second, optional member: a one-member inline body is the
            // single-key facet's, and this plan is about promoted bare payloads.
            .WithOperation("widget.stats", path: "/api/widget/stats", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .AdditionalPropertiesFalse()
                    .Property("count", property => property.Type("integer"), required: true)
                    .Property("label", property => property.Type("string")))
                .Response(400, "application/json", schema => schema.Ref("WidgetError")))
            .WithOperation("widget.handoff", path: "/api/widget/handoff", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .AdditionalPropertiesFalse()
                    .Property("handoff", property => property.Ref("WidgetInfo"), required: true)
                    .Property("note", property => property.Type("string"))))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.stats", "widget.handoff"),
            Curation(Groups("widget", RootGroup())));

        var bound = plan.Clients.SelectMany(static client => client.Operations).ToArray();
        return (
            plan,
            bound.Single(static operation => operation.MethodName == "GetStatsAsync"),
            bound.Single(static operation => operation.MethodName == "GetHandoffAsync"));
    }

    private static async Task<(EmitPlan Plan, ContainerOperations Operations)> CreateContainerEnvelopePlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetListEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property.Type("array").Items(item => item.Ref("WidgetInfo")), required: true))
            .WithSchema("WidgetMapEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property.Type("object").AdditionalProperties(value => value.Ref("WidgetInfo")),
                    required: true))
            .WithSchema("WidgetError", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("_tag", property => property.Type("string").Enum("WidgetError"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithOperation("widget.list", path: "/api/widget/list", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetListEnvelope"))
                .Response(400, "application/json", schema => schema.Ref("WidgetError")))
            .WithOperation("widget.map", path: "/api/widget/map", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetMapEnvelope")))
            .WithOperation("widget.bareList", path: "/api/widget/bare-list", configure: operation => operation
                .Response(200, "application/json", schema => schema.Type("array").Items(item => item.Ref("WidgetInfo"))))
            .WithOperation("widget.bareMap", path: "/api/widget/bare-map", configure: operation => operation
                .Response(200, "application/json",
                    schema => schema.Type("object").AdditionalProperties(value => value.Ref("WidgetInfo"))))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.list", "widget.map", "widget.bareList", "widget.bareMap"),
            Curation(
                Groups("widget", RootGroup()),
                operationNames:
                [
                    OperationName("widget.list", "GetWidgetListAsync"),
                    OperationName("widget.map", "GetWidgetMapAsync"),
                    OperationName("widget.bareList", "GetWidgetBareListAsync"),
                    OperationName("widget.bareMap", "GetWidgetBareMapAsync"),
                ]));

        var bound = plan.Clients.SelectMany(static client => client.Operations).ToArray();
        var operations = new ContainerOperations(
            DataList: bound.Single(static operation => operation.MethodName == "GetWidgetListAsync"),
            DataMap: bound.Single(static operation => operation.MethodName == "GetWidgetMapAsync"),
            BareList: bound.Single(static operation => operation.MethodName == "GetWidgetBareListAsync"),
            BareMap: bound.Single(static operation => operation.MethodName == "GetWidgetBareMapAsync"));
        return (plan, operations);
    }

    private static async Task AssertContainerPayloadShapesAsync(ContainerOperations operations)
    {
        await Assert.That(operations.DataList.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(operations.DataList.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(operations.DataMap.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(operations.DataMap.Envelope.PayloadType).IsTypeOf<DictionaryTypeReferencePlan>();
        await Assert.That(operations.BareList.Envelope!.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(operations.BareList.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(operations.BareMap.Envelope!.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(operations.BareMap.Envelope.PayloadType).IsTypeOf<DictionaryTypeReferencePlan>();
    }

    private static async Task AssertListPayloadRoundTripsAsync(Assembly assembly, OperationPlan operation, string rawBody)
    {
        var envelope = operation.Envelope!;
        var result = AdaptSuccess(assembly, envelope, rawBody);
        var payload = (IList)GetProperty(result, envelope.PayloadName!)!;
        await Assert.That(payload.Count).IsEqualTo(1);
        await Assert.That(GetProperty(payload[0]!, "Id")).IsEqualTo("a");
    }

    private static async Task AssertMapPayloadRoundTripsAsync(Assembly assembly, OperationPlan operation, string rawBody)
    {
        var envelope = operation.Envelope!;
        var result = AdaptSuccess(assembly, envelope, rawBody);
        var payload = (IDictionary)GetProperty(result, envelope.PayloadName!)!;
        await Assert.That(payload.Count).IsEqualTo(1);
        await Assert.That(GetProperty(payload["k"]!, "Id")).IsEqualTo("a");
    }

    private static object AdaptSuccess(Assembly assembly, EnvelopePlan envelope, string rawBody)
    {
        var (adapter, adapt) = ResolveAdapter(assembly, envelope);
        return adapt.Invoke(adapter, [200, rawBody])
               ?? throw new InvalidOperationException("Adapt returned null.");
    }

    private static async Task AssertMissingDataFailsAsync(Assembly assembly, EnvelopePlan envelope, string rawBody)
    {
        var (adapter, adapt) = ResolveAdapter(assembly, envelope);

        var exception = Assert.Throws<TargetInvocationException>(() => _ = adapt.Invoke(adapter, [200, rawBody]));

        await Assert.That(exception.InnerException?.GetType().FullName).IsEqualTo("OpenCode.Sdk.OpenCodeTransportException");
    }

    private static (object Adapter, MethodInfo Adapt) ResolveAdapter(Assembly assembly, EnvelopePlan envelope)
    {
        var adapterType = assembly.GetType($"OpenCode.Sdk.Internal.ResponseAdapters.{envelope.AdapterTypeName}", throwOnError: true)!;
        var adapter = adapterType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                      ?? throw new InvalidOperationException("The generated response adapter has no Instance.");
        var adapt = adapterType.GetMethod("Adapt", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("The generated response adapter has no Adapt method.");
        return (adapter, adapt);
    }

    private sealed record ContainerOperations(
        OperationPlan DataList,
        OperationPlan DataMap,
        OperationPlan BareList,
        OperationPlan BareMap);
}
