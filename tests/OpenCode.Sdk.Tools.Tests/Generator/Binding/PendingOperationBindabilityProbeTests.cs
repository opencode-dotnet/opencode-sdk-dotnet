using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class PendingOperationBindabilityProbeTests
{
    [Test]
    [Arguments("missing", true, null)]
    [Arguments("text", false, "the query parameter is a plain string that no curation row classifies: add a queryParameterTypes row when upstream decodes it as a number, or a stringQueryParameters row that says why it stays a string")]
    [Arguments("mixed", false, "real shape wall")]
    [Arguments("empty", false, "Binding failed with 0 error(s):")]
    public async Task Probe_Should_Classify_By_Refusal_Kind(string variation, bool bindable, string? refusal)
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario);
        var missing = new BindingError(BindingErrorCategory.Curation, "widget.list", "arbitrary missing-row wording") { Kind = BindingErrorKind.MissingRow };
        BindingError[] errors = variation switch
        {
            "missing" => [missing],
            "text" => [new(BindingErrorCategory.Curation, "widget.list", "the query parameter is a plain string that no curation row classifies: add a queryParameterTypes row when upstream decodes it as a number, or a stringQueryParameters row that says why it stays a string")],
            "mixed" => [missing, new(BindingErrorCategory.Operation, "widget.list", "real shape wall")],
            _ => [],
        };

        var mark = new PendingOperationBindabilityProbe(new RefusingSpecBinder(new BindingException(errors)))
            .Probe(document, ["widget.list"]).Single();

        await Assert.That(mark.IsBindable).IsEqualTo(bindable);
        await Assert.That(mark.RefusalMessage).IsEqualTo(refusal);
    }

    private static readonly SpecScenario WidgetScenario = SpecScenario.Define(spec => spec
        .WithSchema("WidgetInfo", schema => schema
            .Type("object")
            .Property("id", property => property.Type("string"), required: true))
        .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
            .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))
        .WithOperation("widget.connect", path: "/api/widget/connect", configure: operation => operation
            .Extension("x-websocket", "true"))
        .WithOperation("widget.tail", method: "put", path: "/api/widget/tail", configure: operation => operation
            .Extension("x-websocket", "true"))
        .WithOperation("widget.item", path: "/api/widget/{class}", configure: operation => operation
            .Parameter("class", "path", schema => schema.Type("string"), required: true)
            .Response(200, "application/json", schema => schema.Ref("WidgetInfo"))));

    /// <summary>A keyword route value is a missing parameterNames row, which the probe filters, so it probes bindable.</summary>
    [Test]
    public async Task Probe_Should_Mark_A_Keyword_Route_Value_As_Bindable()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario);

        var mark = new PendingOperationBindabilityProbe(new BindingTestHost().Binder).Probe(document, ["widget.item"]).Single();

        await Assert.That(mark.IsBindable).IsTrue();
        await Assert.That(mark.RefusalMessage).IsNull();
    }

    [Test]
    public async Task Probe_Should_Mark_A_Standalone_Operation_As_Bindable()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario);
        var probe = new PendingOperationBindabilityProbe(new BindingTestHost().Binder);

        var marks = probe.Probe(document, ["widget.list"]);

        await Assert.That(marks.Count).IsEqualTo(1);
        await Assert.That(marks[0].OperationId).IsEqualTo("widget.list");
        await Assert.That(marks[0].IsBindable).IsTrue();
        await Assert.That(marks[0].RefusalMessage).IsNull();
    }

    [Test]
    public async Task Probe_Should_Mark_A_Walled_Operation_Refused_With_The_Walls_Verbatim_Message()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario);
        var probe = new PendingOperationBindabilityProbe(new BindingTestHost().Binder);

        var marks = probe.Probe(document, ["widget.connect"]);

        await Assert.That(marks.Count).IsEqualTo(1);
        await Assert.That(marks[0].OperationId).IsEqualTo("widget.connect");
        await Assert.That(marks[0].IsBindable).IsFalse();
        await Assert.That(marks[0].RefusalMessage).IsEqualTo("WebSocket operations are not supported");
    }

    [Test]
    public async Task Probe_Should_List_Every_Independent_Wall_In_Binder_Order_When_An_Operation_Fails_Several_Walls()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario);
        var probe = new PendingOperationBindabilityProbe(new BindingTestHost().Binder);

        var marks = probe.Probe(document, ["widget.tail"]);

        await Assert.That(marks.Count).IsEqualTo(1);
        await Assert.That(marks[0].IsBindable).IsFalse();
        await Assert.That(marks[0].RefusalMessage).IsEqualTo(
            "WebSocket operations are not supported; PUT operations must carry a request body");
    }

    [Test]
    public async Task Probe_Should_Collapse_A_Problem_Repeated_Across_Independent_Subjects_To_One_Occurrence()
    {
        var (document, _, _) = await BindingTestHost.LoadPinnedInputsAsync();
        var probe = new PendingOperationBindabilityProbe(new BindingTestHost().Binder);

        var marks = probe.Probe(document, ["config.get"]);

        await Assert.That(marks.Count).IsEqualTo(1);
        await Assert.That(marks[0].IsBindable).IsFalse();
        var walls = marks[0].RefusalMessage!.Split("; ");
        await Assert.That(walls).Contains("inline nominal schema was not promoted into the graph");
        await Assert.That(walls.Count(static wall => wall is "inline nominal schema was not promoted into the graph"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task Probe_Should_Refuse_Without_Crashing_When_The_Binder_Throws_Unexpectedly()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario);
        var probe = new PendingOperationBindabilityProbe(new ThrowingSpecBinder());

        var marks = probe.Probe(document, ["widget.list"]);

        await Assert.That(marks.Count).IsEqualTo(1);
        await Assert.That(marks[0].OperationId).IsEqualTo("widget.list");
        await Assert.That(marks[0].IsBindable).IsFalse();
        await Assert.That(marks[0].RefusalMessage).IsNotNull();
        await Assert.That(marks[0].RefusalMessage).Contains(nameof(InvalidOperationException));
        await Assert.That(marks[0].RefusalMessage).Contains("boom");
    }

    [Test]
    public async Task Probe_Should_Preserve_The_Requested_Operation_Order()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario);
        var probe = new PendingOperationBindabilityProbe(new BindingTestHost().Binder);

        var marks = probe.Probe(document, ["widget.connect", "widget.list"]);

        await Assert.That(marks.Select(static mark => mark.OperationId))
            .IsEquivalentTo(["widget.connect", "widget.list"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("credential.activate")]
    [Arguments("form.list")]
    [Arguments("integration.connect.key")]
    [Arguments("integration.list")]
    [Arguments("integration.oauth.connect")]
    [Arguments("project.update")]
    [Arguments("session.environment")]
    [Arguments("session.form.create")]
    [Arguments("session.form.get")]
    [Arguments("session.form.reply")]
    [Arguments("session.view")]
    public async Task Probe_Should_Mark_A_Wall_Free_Pinned_Operation_As_Bindable(string operationId)
    {
        var (document, _, _) = await BindingTestHost.LoadPinnedInputsAsync();
        var probe = new PendingOperationBindabilityProbe(new BindingTestHost().Binder);

        var marks = probe.Probe(document, [operationId]);

        await Assert.That(marks.Count).IsEqualTo(1);
        await Assert.That(marks[0].IsBindable).IsTrue();
        await Assert.That(marks[0].RefusalMessage).IsNull();
    }

    private sealed class RefusingSpecBinder(BindingException exception) : ISpecBinder
    {
        public EmitPlan Bind(SpecDocument document, OperationSelection selection, GenerationCuration curation) => throw exception;
    }

    private sealed class ThrowingSpecBinder : ISpecBinder
    {
        public EmitPlan Bind(SpecDocument document, OperationSelection selection, GenerationCuration curation) =>
            throw new InvalidOperationException("boom");
    }
}
