using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The generated string enums are extensible: a value a newer server sends is kept as it
/// arrived and written back unchanged, a non-string token is still a malformed body, and a default
/// value has no spelling. One generated enum stands for all of them, because the emitter renders
/// every enum the same way; the event case proves that an unknown value inside a stream frame no
/// longer fails the frame.
/// </summary>
public sealed class ExtensibleEnumContractTests
{
    private const string UnknownStatus = "copied";

    private static readonly FixtureLoader Fixtures = new();

    [Test]
    public async Task Deserialize_Should_Keep_A_Value_The_Pin_Does_Not_Define()
    {
        var diff = Read("Serialization.unknown-diff-status.json");

        await Assert.That(diff.Status.ToString()).IsEqualTo(UnknownStatus);
        await Assert.That(diff.Status.IsKnown).IsFalse();
        await Assert.That(diff.Status).IsEqualTo(new FileDiffInfoStatus(UnknownStatus));
    }

    [Test]
    public async Task Serialize_Should_Write_An_Unknown_Value_Back_Unchanged()
    {
        var diff = Read("Serialization.unknown-diff-status.json");

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(diff, OpenCodeJsonContext.Default.FileDiffInfo));

        await Assert.That(written.RootElement.GetProperty("status").GetString()).IsEqualTo(UnknownStatus);
    }

    [Test]
    public async Task Deserialize_Should_Read_A_Pinned_Value_As_Its_Static_Property()
    {
        var diff = Read("Serialization.known-diff-status.json");

        await Assert.That(diff.Status).IsEqualTo(FileDiffInfoStatus.Modified);
        await Assert.That(diff.Status.IsKnown).IsTrue();
    }

    /// <summary>
    /// The pinned spellings are constants, so a value reads in a <c>case</c> label through its
    /// <c>Value</c>: the pinned spelling takes its case, and a newer server's value falls through to
    /// the default arm instead of failing.
    /// </summary>
    [Test]
    [Arguments("Serialization.known-diff-status.json", "modified")]
    [Arguments("Serialization.unknown-diff-status.json", "other: copied")]
    public async Task Value_Should_Match_A_Case_Label_Of_The_Pinned_Constants(string fixture, string expected)
    {
        var diff = Read(fixture);

        var matched = diff.Status.Value switch
        {
            FileDiffInfoStatus.Values.Modified => "modified",
            FileDiffInfoStatus.Values.Added => "added",
            var other => "other: " + other,
        };

        await Assert.That(matched).IsEqualTo(expected);
    }

    [Test]
    public async Task Value_Should_Match_A_Property_Pattern_Of_A_Pinned_Constant()
    {
        var diff = Read("Serialization.known-diff-status.json");

        await Assert.That(diff is { Status.Value: FileDiffInfoStatus.Values.Modified }).IsTrue();
        await Assert.That(diff is { Status.Value: FileDiffInfoStatus.Values.Deleted }).IsFalse();
    }

    /// <summary>The constant is the wire spelling the pinned document declares, the one the static property carries.</summary>
    [Test]
    public async Task Values_Should_Hold_The_Pinned_Wire_Spellings()
    {
        await Assert.That(FileDiffInfoStatus.Values.Modified).IsEqualTo("modified");
        await Assert.That(FileDiffInfoStatus.Modified.Value).IsEqualTo(FileDiffInfoStatus.Values.Modified);
    }

    [Test]
    public async Task Value_Should_Be_Empty_For_A_Default_Value_As_ToString_Is() =>
        await Assert.That(new FileDiffInfoStatus().Value).IsEqualTo(string.Empty);

    [Test]
    [Arguments("Serialization.numeric-diff-status.json")]
    [Arguments("Serialization.null-diff-status.json")]
    public async Task Deserialize_Should_Refuse_A_Token_That_Is_Not_A_String(string fixture) =>
        _ = await Assert.That(() => Read(fixture)).Throws<JsonException>();

    [Test]
    public async Task Serialize_Should_Refuse_A_Default_Value() =>
        _ = await Assert.That(() => JsonSerializer.Serialize(new FileDiffInfoStatus(), OpenCodeJsonContext.Default.FileDiffInfoStatus))
            .Throws<JsonException>();

    [Test]
    public async Task Equals_Should_Compare_Spellings_Ordinally()
    {
        await Assert.That(new FileDiffInfoStatus("modified")).IsEqualTo(FileDiffInfoStatus.Modified);
        await Assert.That(new FileDiffInfoStatus("Modified") == FileDiffInfoStatus.Modified).IsFalse();
        await Assert.That(new FileDiffInfoStatus("modified").GetHashCode()).IsEqualTo(FileDiffInfoStatus.Modified.GetHashCode());
    }

    [Test]
    public async Task Conversion_Should_Read_A_Spelling_As_A_Value()
    {
        FileDiffInfoStatus converted = UnknownStatus;

        await Assert.That(converted).IsEqualTo(FileDiffInfoStatus.FromString(UnknownStatus));
        await Assert.That(new FileDiffInfoStatus().ToString()).IsEmpty();
    }

    /// <summary>
    /// A durable event whose enum member carries a value this SDK does not know still reads as its
    /// typed arm, so a newer server's value no longer ends the subscription that carries it.
    /// </summary>
    [Test]
    public async Task Deserialize_Should_Read_An_Event_Whose_Enum_Value_Is_Unknown()
    {
        var @event = JsonSerializer.Deserialize(
            Fixtures.LoadJson("Serialization.unknown-interrupted-reason-event.json"),
            OpenCodeJsonContext.Default.IEvent);

        var interrupted = await Assert.That(@event).IsTypeOf<SessionExecutionInterrupted>();
        await Assert.That(interrupted!.Data.Reason.ToString()).IsEqualTo("watchdog");
        await Assert.That(interrupted.Data.Reason.IsKnown).IsFalse();
    }

    private static FileDiffInfo Read(string fixture) =>
        JsonSerializer.Deserialize(Fixtures.LoadJson(fixture), OpenCodeJsonContext.Default.FileDiffInfo)!;
}
