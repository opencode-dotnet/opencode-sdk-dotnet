using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ModelEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_Immutable_Annotated_Models()
    {
        var sources = ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot());

        await Verify(EmitterSnapshot.Create(sources));
    }

    [Test]
    public async Task Emit_Should_Use_Shallow_Init_Only_Collection_References()
    {
        var source = EmitterSnapshot.Create(ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot()));

        await Assert.That(source).Contains("public IReadOnlyList<string>? Tags { get; init; }");
        await Assert.That(source).Contains("public IReadOnlyDictionary<string, Uri>? Links { get; init; }");
        await Assert.That(source).Contains("public required IReadOnlyList<string> RequiredTags { get; init; }");
        await Assert.That(source).DoesNotContain("OptionalCollectionInput");
        await Assert.That(source).DoesNotContain("new ReadOnlyDictionary");
        await Assert.That(source).DoesNotContain("new List");
    }

    [Test]
    public async Task Emit_Should_Write_Null_Only_For_Required_Nullable_Properties()
    {
        var source = EmitterSnapshot.Create(ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot()));

        await Assert.That(source).Contains("[JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]");
        await Assert.That(source).Contains("public required string? RequiredNullable { get; init; }");
        await Assert.That(source).DoesNotContain("WireNullRejecting");
    }

    [Test]
    public async Task Emit_Should_Emit_A_Prefix_Discriminator_As_A_Guarded_Required_String()
    {
        var sources = ModelEmitter.Emit(EmitterPlanFixture.Create());
        var arm = EmitterSnapshot.Content(sources, "Example/Models/RpcEvent.cs");

        await Assert.That(arm).Contains("[JsonPropertyName(\"type\")]");
        await Assert.That(arm).Contains("public required string Type");
        await Assert.That(arm).Contains("if (!value.StartsWith(\"rpc.\", StringComparison.Ordinal))");
        await Assert.That(arm).Contains("The 'type' marker must carry the 'rpc.' prefix.");
        await Assert.That(arm).Contains("field = value;");
        await Assert.That(arm).DoesNotContain("public string Type => \"");
    }

    /// <summary>
    /// An open model keeps its named members and adds one extension-data member typed the way
    /// System.Text.Json requires (<c>IDictionary</c>, not the read-only interface every other
    /// dictionary member uses) and shaped the way its source generator can fill it (get-only
    /// over a pre-built bag, never init-only), so the wire members the document leaves open
    /// survive a round trip.
    /// </summary>
    [Test]
    public async Task Emit_Should_Render_An_Extension_Data_Member_For_An_Open_Model()
    {
        var sources = ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot());
        var model = EmitterSnapshot.Content(sources, "Example/Models/OpenSettings.cs");

        await Assert.That(model).Contains("using OpenCode.Sdk.Internal;");
        await Assert.That(model).Contains("using System;");
        await Assert.That(model).Contains("using System.Collections.Generic;");
        await Assert.That(model).Contains("using System.Linq;");
        await Assert.That(model).Contains("using System.Text.Json;");
        await Assert.That(model).Contains("public double? Timeout { get; init; }");
        // The public view: read-only typed, init-only, invisible to the serializer, never null.
        await Assert.That(model).Contains("[JsonIgnore]");
        await Assert.That(model).Contains("public IReadOnlyDictionary<string, JsonElement> AdditionalProperties");
        await Assert.That(model).Contains("get => OpenMembers ?? EmptyOpenMembers.Instance;");
        await Assert.That(model).Contains("OpenMembers = value.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);");
        // The serializer's bag: settable, internal, admitted by JsonInclude, created lazily.
        await Assert.That(model).Contains("[JsonExtensionData]");
        await Assert.That(model).Contains("[JsonInclude]");
        await Assert.That(model).Contains("internal Dictionary<string, JsonElement>? OpenMembers { get; set; }");
        await Assert.That(model).DoesNotContain("new Dictionary");
        await Assert.That(model).DoesNotContain("public IDictionary");
        await Assert.That(model).DoesNotContain("[JsonPropertyName(\"additionalProperties\")]");
    }
}
