using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator;

public sealed class PartialMarkerComposerTests
{
    private const string WebSocketRefusal = "WebSocket operations are not supported";

    [Test]
    public async Task Compose_Should_Preserve_Sections_And_Verbatim_Walls()
    {
        var marker = PartialMarkerComposer.Compose(
            ["session.list", "session.get", "session.update"],
            [Mark("command.connect", WebSocketRefusal), Mark("command.list")],
            [
                new() { OperationId = "widget.connect", Reason = "Requires a WebSocket upgrade." },
                new() { OperationId = "widget.tail", Reason = "Cannot express the PUT response." },
            ],
            [
                Mark("widget.connect", WebSocketRefusal),
                Mark("widget.tail", WebSocketRefusal + "; PUT operations must carry a request body"),
            ],
            ["terminal.attach", "terminal.connect"]);

        await Assert.That(marker).IsEqualTo("""
            Generation is incomplete; packages must not be published.
            Selected operations: 3
            Pending operations: 2
            Declined operations: 2
            Transport-owned operations: 2
            Selected:
            - session.list
            - session.get
            - session.update
            Pending:
            - command.connect [refused: WebSocket operations are not supported]
            - command.list [bindable]
            Declined:
            - widget.connect [declined: Requires a WebSocket upgrade.] [refused: WebSocket operations are not supported]
            - widget.tail [declined: Cannot express the PUT response.] [refused: WebSocket operations are not supported; PUT operations must carry a request body]
            Transport-owned:
            - terminal.attach [fingerprint-pinned]
            - terminal.connect [fingerprint-pinned]
            """ + "\n");
    }

    [Test]
    public async Task RefuseBindableDeclines_Should_Report_Ordered_Errors()
    {
        var exception = Assert.Throws<BindingException>(() => PartialMarkerComposer.RefuseBindableDeclines(
            [Mark("widget.get"), Mark("widget.tail", WebSocketRefusal), Mark("widget.update")]));

        await Assert.That(exception.Errors).IsEquivalentTo(new BindingError[]
        {
            new(BindingErrorCategory.Curation, "widget.get", "a bindable operation cannot be declined — select it or leave it pending"),
            new(BindingErrorCategory.Curation, "widget.update", "a bindable operation cannot be declined — select it or leave it pending"),
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RefuseUnplacedModels_Should_Report_Ordered_Errors()
    {
        var placement = ModelPlacement.Empty with { Unplaced = ["AlphaModel", "ZuluModel"] };

        var exception = Assert.Throws<BindingException>(() => PartialMarkerComposer.RefuseUnplacedModels(placement));

        await Assert.That(exception.Errors).IsEquivalentTo(new BindingError[]
        {
            new(BindingErrorCategory.Curation, "AlphaModel", "the model has no family prefix and no modelOwners row: add a row naming its owner, with its reason"),
            new(BindingErrorCategory.Curation, "ZuluModel", "the model has no family prefix and no modelOwners row: add a row naming its owner, with its reason"),
        }, CollectionOrdering.Matching);
    }

    private static PendingOperationMark Mark(string operationId, string? refusal = null) =>
        new()
        {
            OperationId = operationId,
            IsBindable = refusal is null,
            RefusalMessage = refusal,
        };
}
