using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

/// <summary>
/// The structural gate every future spec refresh runs through: representative landmarks at
/// the known lossy seams, no count assertions. A red landmark is evidence about the wire,
/// never a reason to patch the assertion.
/// </summary>
public sealed class PinnedSpecSmokeTests
{
    [Test]
    public async Task Ingest_Should_Absorb_The_Pinned_Spec()
    {
        var document = await IngestPinnedSpecAsync();

        await Assert.That(document.Operations).IsNotEmpty();
        await Assert
            .That(document.Operations.All(static operation =>
                !operation.OperationId.StartsWith("v2.", StringComparison.Ordinal)))
            .IsTrue();
    }

    [Test]
    public async Task Ingest_Should_Flag_Stream_Wildcard_And_WebSocket_Operations()
    {
        var document = await IngestPinnedSpecAsync();

        var sessionLog = document.Operations.Single(static operation => operation.OperationId == "session.log");
        await Assert.That(sessionLog.IsSse).IsTrue();
        await Assert.That(sessionLog.Responses.Single(static response => response.IsSse).EffectStream).IsNotNull();
        await Assert
            .That(document.Operations.Single(static operation => operation.OperationId == "event.subscribe").IsSse)
            .IsTrue();
        await Assert
            .That(document.Operations.Single(static operation => operation.OperationId == "fs.read").HasWildcardPath)
            .IsTrue();
        await Assert
            .That(document.Operations.Single(static operation => operation.OperationId == "pty.connect").IsWebSocket)
            .IsTrue();
    }

    [Test]
    public async Task Ingest_Should_Classify_Marked_Nested_And_Structural_Union_Landmarks()
    {
        var document = await IngestPinnedSpecAsync();

        var durableEvent = (UnionNode)document.Schemas["Session.Event.Durable"];
        await Assert.That(durableEvent.Keyword).IsEqualTo(UnionKeyword.OneOf);
        await Assert.That(durableEvent.Classification).IsEqualTo(UnionClassification.Marked);

        var message = (UnionNode)document.Schemas["Session.Message.Info"];
        await Assert.That(message.Classification).IsEqualTo(UnionClassification.Marked);

        var compaction = (UnionNode)document.Schemas["Session.Message.Compaction"];
        await Assert.That(compaction.Classification).IsEqualTo(UnionClassification.Marked);

        var formatter = (UnionNode)document.Schemas["Config.InfoEncoded#/properties/formatter"];
        await Assert.That(formatter.Classification).IsEqualTo(UnionClassification.Structural);

        var fields = (ArrayNode)document.Schemas["Form.Fields"];
        await Assert.That(fields.Item).IsTypeOf<RefNode>();
        await Assert.That(((RefNode)fields.Item).Target).IsEqualTo("Form.Field");
    }

    [Test]
    public async Task Ingest_Should_Project_Special_Number_And_Unrestricted_Landmarks()
    {
        var document = await IngestPinnedSpecAsync();

        var shell = (ObjectNode)document.Schemas["Session.Message.Shell"];
        await Assert
            .That(shell.Properties.Single(static property => property.Name == "exit").Schema)
            .IsTypeOf<SpecialNumberNode>();

        var instructionEntry = (ObjectNode)document.Schemas["InstructionEntry.Info"];
        await Assert
            .That(instructionEntry.Properties.Single(static property => property.Name == "value").Schema)
            .IsTypeOf<UnrestrictedNode>();
    }

    [Test]
    public async Task Ingest_Should_Project_The_Pinned_Stream_Cause_Schema()
    {
        var document = await IngestPinnedSpecAsync();

        foreach (var operationId in new[] { "session.log", "event.subscribe", })
        {
            var causeKey = $"op:{operationId}#/responses/200/content/text~1event-stream/x-effect-stream/causeSchema/items";
            var found = document.Schemas.TryGetValue(causeKey, out var causeNode);
            await Assert.That(found).IsTrue();
            await Assert.That(causeNode).IsTypeOf<UnionNode>();

            var cause = (UnionNode)causeNode!;
            await Assert.That(cause.Classification).IsEqualTo(UnionClassification.Marked);
            await Assert.That(cause.Branches).Count().IsEqualTo(3);

            var fail = CauseBranch(document, cause, "Fail");
            var failError = fail.Properties.Single(static property => property.Name == "error");
            await Assert.That(failError.IsRequired).IsTrue();
            await Assert.That(failError.Schema).IsTypeOf<NeverNode>();

            var die = CauseBranch(document, cause, "Die");
            var defect = die.Properties.Single(static property => property.Name == "defect");
            await Assert.That(defect.IsRequired).IsTrue();
            await Assert.That(defect.Schema).IsTypeOf<UnrestrictedNode>();

            var interrupt = CauseBranch(document, cause, "Interrupt");
            var fiberId = interrupt.Properties.Single(static property => property.Name == "fiberId");
            await Assert.That(fiberId.IsRequired).IsTrue();
            await Assert.That(fiberId.Schema).IsTypeOf<NullableNode>();
            await Assert.That(((NullableNode)fiberId.Schema).Inner).IsTypeOf<PrimitiveNode>();
            await Assert.That(((PrimitiveNode)((NullableNode)fiberId.Schema).Inner).Kind).IsEqualTo(PrimitiveKind.Number);
        }
    }

    [Test]
    public async Task Ingest_Should_Classify_A_Tagged_Error_That_Also_Carries_A_Name_As_Effect_Tagged()
    {
        // WorktreeError is a tagged error whose fields keep the former name/data shape, so it
        // carries both markers; the _tag marker decides.
        var document = await IngestPinnedSpecAsync();

        await Assert.That(((ObjectNode)document.Schemas["WorktreeErrorEncoded"]).ErrorStyle).IsEqualTo(ErrorStyle.EffectTag);
        await Assert.That(((ObjectNode)document.Schemas["UnauthorizedErrorEncoded"]).ErrorStyle).IsEqualTo(ErrorStyle.EffectTag);
    }

    [Test]
    public async Task Ingest_Should_Classify_Envelope_Shapes()
    {
        var document = await IngestPinnedSpecAsync();

        await Assert.That(EnvelopeOf(document, "session.list")).IsEqualTo(SpecEnvelopeShape.CursorData);
        await Assert.That(EnvelopeOf(document, "agent.list")).IsEqualTo(SpecEnvelopeShape.DataLocation);
        await Assert.That(EnvelopeOf(document, "session.message.get")).IsEqualTo(SpecEnvelopeShape.Data);
    }

    [Test]
    public async Task Ingest_Should_Be_Deterministic_Across_Repeated_Runs()
    {
        var first = await IngestPinnedSpecAsync();
        var second = await IngestPinnedSpecAsync();

        await Assert.That(first.Schemas.Keys.SequenceEqual(second.Schemas.Keys, StringComparer.Ordinal)).IsTrue();
        await Assert
            .That(first
                .Operations.Select(static operation => operation.OperationId)
                .SequenceEqual(second.Operations.Select(static operation => operation.OperationId), StringComparer.Ordinal))
            .IsTrue();
    }

    private static SpecEnvelopeShape EnvelopeOf(SpecDocument document, string operationId) =>
        document
            .Operations.Single(operation => operation.OperationId == operationId)
            .Responses.Single(static response => response.StatusCode == 200)
            .EnvelopeShape;

    private static ObjectNode CauseBranch(SpecDocument document, UnionNode cause, string tag)
    {
        var reference = cause
            .Branches
            .OfType<RefNode>()
            .Single(branch => branch.Target.EndsWith($"/_tag={tag}", StringComparison.Ordinal));
        return (ObjectNode)document.Schemas[reference.Target];
    }

    private static Task<SpecDocument> IngestPinnedSpecAsync() => BindingTestHost.IngestPinnedAsync();
}
