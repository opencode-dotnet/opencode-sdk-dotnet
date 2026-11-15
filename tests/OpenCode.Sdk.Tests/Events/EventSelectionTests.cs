using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

public sealed class EventSelectionTests
{
    [Test]
    public async Task Capture_Should_Copy_The_Callers_Types()
    {
        var types = new List<EventType> { new("session.created"), new("rpc.sdk-test") };
        var selection = EventSelection.Capture(types);
        types.Clear();
        types.Add(new EventType("rpc.other"));

        string[] candidates = ["session.updated", "session.created", "rpc.other", "rpc.sdk-test", "future.event", "server.connected"];
        await Assert.That(candidates.Where(selection.Includes))
            .IsEquivalentTo(["session.created", "rpc.sdk-test", "server.connected"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Includes_Should_Select_All_Types_When_The_Selection_Is_Null_Or_Empty(bool empty)
    {
        var selection = EventSelection.Capture(empty ? [] : null);
        string[] candidates = ["session.created", "rpc.other", "future.event", "server.connected"];

        await Assert.That(candidates.Where(selection.Includes))
            .IsEquivalentTo(["session.created", "rpc.other", "future.event", "server.connected"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Includes_Should_Select_Exact_Ordinal_Types_And_The_Marker()
    {
        var selection = EventSelection.Capture([new("session.created"), new("rpc.sdk-test"), new("future.event"), new("")]);
        string[] candidates =
            ["Session.Created", "session.updated", "session.created", "rpc.other", "rpc.sdk-test", "future.event", "server.connected", ""];

        await Assert.That(candidates.Where(selection.Includes))
            .IsEquivalentTo(["session.created", "rpc.sdk-test", "future.event", "server.connected", ""], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Capture_Should_Refuse_A_Default_EventType()
    {
        var exception = await Assert.That(() => EventSelection.Capture([default])).Throws<ArgumentException>();

        await Assert.That(exception!.Message).StartsWith("A default EventType has no wire value.");
        await Assert.That(exception.ParamName).IsEqualTo("request");
    }
}
