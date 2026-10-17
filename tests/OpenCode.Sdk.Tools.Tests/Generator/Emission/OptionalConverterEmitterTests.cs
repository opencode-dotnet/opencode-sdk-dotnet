using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

/// <summary>
/// The tri-state wrapper is generic and a <c>[JsonConverter]</c> argument cannot name an unbound
/// generic, so every instantiation that reaches the wire needs its own closed converter. These
/// pin the two halves that cannot be read off the models: one converter per distinct
/// instantiation, and a body that binds metadata through the emitted context rather than through
/// the reflection path.
/// </summary>
[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class OptionalConverterEmitterTests(PinnedBinding pinned)
{
    [Test]
    public async Task Emit_Should_Keep_Plain_And_Special_Number_Converters_Distinct_And_Ordered()
    {
        var model = Record("WidgetPatchRequest",
            Property("special", SpecialNumber(isNullable: true), isRequired: false, emitsOptionalWrapper: true),
            Property("plain", Named("double", isNullable: true), isRequired: false, emitsOptionalWrapper: true));

        var sources = OptionalConverterEmitter.Emit([model]);

        await Assert.That(sources.Select(static source => source.RelativePath)).IsEquivalentTo(
        [
            "Internal/Serialization/OptionalOfDoubleJsonConverter.cs",
            "Internal/Serialization/OptionalOfSpecialNumberJsonConverter.cs",
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Emit_Should_Refuse_A_Wrapped_Container_Of_Special_Numbers(bool dictionary)
    {
        var special = SpecialNumber();
        TypeReferencePlan container = dictionary
            ? new DictionaryTypeReferencePlan { ValueType = special, IsNullable = true, JsonNullRepresentation = JsonNullRepresentation.ClrNull, }
            : new ListTypeReferencePlan { ElementType = special, IsNullable = true, JsonNullRepresentation = JsonNullRepresentation.ClrNull, };
        var model = Record("WidgetPatchRequest", Property("values", container, isRequired: false, emitsOptionalWrapper: true));

        var exception = Assert.Throws<InvalidOperationException>(() => OptionalConverterEmitter.Emit([model]));

        await Assert.That(exception.Message).IsEqualTo(
            "No optional converter name exists for plan 'SpecialNumberTypeReferencePlan'; admit the representation first.");
    }

    [Test]
    public async Task Emit_Should_Produce_One_Converter_Per_Distinct_Instantiation()
    {
        ModelPlan[] models =
        [
            Record("WidgetCreateRequest",
                Property("title", Named("string", isNullable: true), isRequired: false, emitsOptionalWrapper: true),
                Property("retain", Named("bool", isNullable: true), isRequired: false, emitsOptionalWrapper: true)),
            Record("WidgetPatchRequest",
                Property("note", Named("string", isNullable: true), isRequired: false, emitsOptionalWrapper: true),
                Property("label", Named("string", isNullable: true), isRequired: false)),
        ];

        var sources = OptionalConverterEmitter.Emit(models);

        // This checks the complete inventory, not its enumeration order.
        await Assert
            .That(sources.Select(static source => source.RelativePath))
            .IsEquivalentTo(
            [
                "Internal/Serialization/OptionalOfBooleanJsonConverter.cs",
                "Internal/Serialization/OptionalOfStringJsonConverter.cs",
            ], CollectionOrdering.Any);
    }

    [Test]
    public async Task Emit_Should_Read_And_Write_The_Explicit_Null_State_For_A_Scalar()
    {
        var source = EmitPinned("OptionalOfStringJsonConverter");

        await Assert.That(source).Contains("internal sealed class OptionalOfStringJsonConverter : JsonConverter<Optional<string?>>");
        await Assert.That(source).Contains("public override bool HandleNull => true;");
        await Assert.That(source).Contains("if (reader.TokenType == JsonTokenType.Null)");
        await Assert.That(source).Contains("return Optional<string?>.Null;");
        await Assert.That(source).Contains("return new Optional<string?>(reader.GetString());");
        await Assert.That(source).Contains("writer.WriteNullValue();");
        await Assert.That(source).Contains("writer.WriteStringValue(value.Value);");
    }

    [Test]
    public async Task Emit_Should_Bind_A_Container_Instantiation_Through_The_Generated_Context()
    {
        var source = EmitPinned("OptionalOfPermissionRuleListJsonConverter");

        await Assert.That(source).Contains("OpenCodeJsonContext.Default.GetTypeInfo(typeof(IReadOnlyList<PermissionRule>))");
        await Assert.That(source).Contains("JsonSerializer.Deserialize(ref reader, typeInfo)");
        await Assert.That(source).Contains("JsonSerializer.Serialize(writer, value.Value, typeInfo);");

        // The reflection overloads take JsonSerializerOptions; a generated converter never does.
        await Assert.That(source).DoesNotContain("JsonSerializer.Deserialize(ref reader, typeof");
        await Assert.That(source).DoesNotContain(", options);");
    }

    private string EmitPinned(string converterName)
    {
        var source = OptionalConverterEmitter
            .Emit(pinned.Plan.Models)
            .Single(candidate => candidate.RelativePath == $"Internal/Serialization/{converterName}.cs");
        return Encoding.UTF8.GetString(source.Utf8Source.Span);
    }
}
