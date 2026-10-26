using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Serialization.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

/// <summary>
/// Characterizes the converter behind every <c>IReadOnlyDictionary&lt;string, JsonElement&gt;</c>
/// member: a value reads as System.Text.Json's own dictionary converter gives it.
/// </summary>
/// <remarks>
/// One non-disposable document backs a dictionary's non-literal values. The cases cover lifetime,
/// the concrete type, clone and equality, duplicates, order, the empty object, nulls and the written
/// bytes. One tool state stands for every such member, because all bind the converter through the context.
/// </remarks>
public sealed class JsonElementDictionaryJsonConverterTests
{
    private const string ValueKindsFixture = "Serialization.json-element-dictionary-value-kinds.json";

    private const string DuplicateKeysFixture = "Serialization.json-element-dictionary-duplicate-keys.json";

    private const string EmptyObjectFixture = "Serialization.json-element-dictionary-empty-object.json";

    private readonly FixtureLoader _fixtures = new();

    private readonly GeneratedJsonSerializer _serializer = new();

    public static IEnumerable<Func<ExpectedJsonValue>> ValueKinds() =>
    [
        () => new ExpectedJsonValue("text", JsonValueKind.String, "\"hello\""),
        () => new ExpectedJsonValue("count", JsonValueKind.Number, "3"),
        () => new ExpectedJsonValue("flag", JsonValueKind.True, "true"),
        () => new ExpectedJsonValue("none", JsonValueKind.Null, "null"),
        () => new ExpectedJsonValue("list", JsonValueKind.Array, "[1,\"two\"]"),
        () => new ExpectedJsonValue("nested", JsonValueKind.Object, "{\"k\":\"v\"}"),
        () => new ExpectedJsonValue("flagAgain", JsonValueKind.True, "true"),
        () => new ExpectedJsonValue("off", JsonValueKind.False, "false"),
        () => new ExpectedJsonValue("offAgain", JsonValueKind.False, "false"),
        () => new ExpectedJsonValue("noneAgain", JsonValueKind.Null, "null"),
    ];

    /// <summary>
    /// Each value outlives the reader and the caller's buffer: the document is a copy the
    /// dictionary owns, and nothing disposes it.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ValueKinds))]
    public async Task Read_Should_Keep_A_Value_Readable_After_The_Source_Buffer_Is_Cleared(ExpectedJsonValue expected)
    {
        var utf8 = Encoding.UTF8.GetBytes(_fixtures.LoadJson(ValueKindsFixture));
        var state = JsonSerializer.Deserialize(utf8, OpenCodeJsonContext.Default.SessionMessageToolStateCompleted)
                    ?? throw new JsonException("The value-kinds fixture deserialized to null.");
        Array.Clear(utf8, 0, utf8.Length);

        var value = state.Input[expected.Key];

        await Assert.That(value.ValueKind).IsEqualTo(expected.Kind);
        await Assert.That(value.GetRawText()).IsEqualTo(expected.RawText);
    }

    /// <summary>
    /// The runtime type is the one System.Text.Json creates, so a caller that casts it can change
    /// it, and the values it keeps stay readable from the shared document.
    /// </summary>
    [Test]
    public async Task Read_Should_Create_A_Dictionary_A_Cast_Can_Change_Without_Disturbing_The_Other_Values()
    {
        var state = Read(ValueKindsFixture);

        await Assert.That(state.Input).IsTypeOf<Dictionary<string, JsonElement>>();
        var dictionary = (Dictionary<string, JsonElement>)state.Input;
        var removed = dictionary["list"];
        dictionary["text"] = dictionary["count"];
        dictionary["added"] = dictionary["nested"];
        _ = dictionary.Remove("list");

        await Assert.That(dictionary).DoesNotContainKey("list");
        await Assert.That(removed.GetRawText()).IsEqualTo(RawTextOf("list"));
        await Assert.That(dictionary["text"].GetRawText()).IsEqualTo(RawTextOf("count"));
        await Assert.That(dictionary["added"].GetRawText()).IsEqualTo(RawTextOf("nested"));
        await Assert.That(dictionary["count"].GetRawText()).IsEqualTo(RawTextOf("count"));
        await Assert.That(dictionary["flag"].GetRawText()).IsEqualTo(RawTextOf("flag"));
        await Assert.That(dictionary["none"].GetRawText()).IsEqualTo(RawTextOf("none"));
        await Assert.That(dictionary["nested"].GetRawText()).IsEqualTo(RawTextOf("nested"));
    }

    /// <summary>
    /// Clone keeps the text, two reads of one body are deeply equal value by value, repeated
    /// literals are equal by default, and record equality still compares the dictionary by reference.
    /// </summary>
    [Test]
    public async Task Read_Should_Keep_Clone_Deep_Equality_And_Reference_Record_Equality()
    {
        var first = Read(ValueKindsFixture);
        var second = Read(ValueKindsFixture);
        var deepEquality = EqualityComparer<JsonElement>.Create(JsonElement.DeepEquals);

        foreach (var entry in first.Input)
        {
            await Assert.That(entry.Value.Clone().GetRawText()).IsEqualTo(entry.Value.GetRawText());
            await Assert.That(second.Input[entry.Key]).IsEqualTo(entry.Value, deepEquality);
        }

        await Assert.That(first.Input.Values.Distinct().Count()).IsEqualTo(7);
        await Assert.That(first with { }).IsEqualTo(first);
        await Assert.That(first with { Input = second.Input, }).IsNotEqualTo(first);
    }

    [Test]
    public async Task Read_Should_Keep_The_First_Position_And_The_Last_Value_Of_A_Duplicate_Key()
    {
        var state = Read(DuplicateKeysFixture);
        string[] expectedKeys = ["a", "b"];

        await Assert.That(state.Input.Keys.ToArray()).IsEquivalentTo(expectedKeys, CollectionOrdering.Matching);
        await Assert.That(state.Input["a"].GetRawText()).IsEqualTo("3");
        await Assert.That(state.Input["b"].GetRawText()).IsEqualTo("2");
    }

    /// <summary>
    /// The SDK never turns duplicates off; a context that does sees the converter refuse them, as
    /// System.Text.Json's own dictionary converter does.
    /// </summary>
    /// <remarks>
    /// Options passed to a context's constructor do not receive the source-generation converters,
    /// so the test adds the converter itself instead of reading through System.Text.Json's own.
    /// </remarks>
    [Test]
    public async Task Read_Should_Throw_JsonException_For_A_Duplicate_Key_When_The_Options_Disallow_Duplicates()
    {
        var options = new JsonSerializerOptions { AllowDuplicateProperties = false, };
        options.Converters.Add(new JsonElementDictionaryJsonConverter());
        var context = new OpenCodeJsonContext(options);
        var json = _fixtures.LoadJson(DuplicateKeysFixture);

        _ = await Assert.That(() => JsonSerializer.Deserialize(json, context.SessionMessageToolStateCompleted))
            .ThrowsExactly<JsonException>();
    }

    [Test]
    public async Task Read_Should_Enumerate_The_Entries_In_Json_Order()
    {
        var state = Read(ValueKindsFixture);
        string[] expected = [.. ValueKinds().Select(static row => row().Key)];

        await Assert.That(state.Input.Select(static entry => entry.Key).ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// An empty object needs no document, but each read still gets a dictionary of its own, and
    /// the reader stops on the object's end so the next member is read.
    /// </summary>
    [Test]
    public async Task Read_Should_Return_A_New_Empty_Dictionary_And_Read_The_Next_Member_For_An_Empty_Object()
    {
        var state = Read(EmptyObjectFixture);
        var other = Read(EmptyObjectFixture);

        await Assert.That(state.Input).IsTypeOf<Dictionary<string, JsonElement>>();
        await Assert.That(state.Input.Count).IsEqualTo(0);
        await Assert.That(other.Input).IsNotSameReferenceAs(state.Input);
        await Assert.That(state.Metadata!["after"].ValueKind).IsEqualTo(JsonValueKind.True);
    }

    [Test]
    public async Task Read_Should_Keep_A_Null_Entry_And_Leave_A_Null_Optional_Member_Null()
    {
        var state = Read("Serialization.json-element-dictionary-null-entry.json");
        string[] expectedKeys = ["cleared"];

        await Assert.That(state.Input.Keys.ToArray()).IsEquivalentTo(expectedKeys, CollectionOrdering.Matching);
        await Assert.That(state.Input["cleared"].ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(state.Metadata).IsNull();
    }

    [Test]
    [Arguments("Serialization.json-element-dictionary-null-required.json")]
    [Arguments("Serialization.json-element-dictionary-array-token.json")]
    [Arguments("Serialization.json-element-dictionary-string-token.json")]
    public async Task Read_Should_Throw_JsonException_When_A_Required_Member_Is_Null_Or_Not_An_Object(string fixtureName)
    {
        var json = _fixtures.LoadJson(fixtureName);

        _ = await Assert.That(() => _serializer.Deserialize<SessionMessageToolStateCompleted>(json))
            .ThrowsExactly<JsonException>();
    }

    /// <summary>
    /// The writer re-encodes every key and string value as System.Text.Json's dictionary converter
    /// does, so the wire spelling of the read body does not survive and no value is dropped.
    /// </summary>
    [Test]
    public async Task Write_Should_Write_Every_Entry_In_Order_Through_The_Writer_Encoder()
    {
        var request = _serializer.Deserialize<SessionCreateRequest>(_fixtures.LoadJson("Serialization.json-element-dictionary-write-input.json"));

        var written = _serializer.Serialize(request);

        await Assert.That(written).IsEqualTo(_fixtures.LoadJson("Serialization.json-element-dictionary-write-expected.json"));
    }

    private static string RawTextOf(string key) =>
        ValueKinds().Select(static row => row()).Single(row => string.Equals(row.Key, key, StringComparison.Ordinal)).RawText;

    private SessionMessageToolStateCompleted Read(string fixtureName) =>
        _serializer.Deserialize<SessionMessageToolStateCompleted>(_fixtures.LoadJson(fixtureName));
}
