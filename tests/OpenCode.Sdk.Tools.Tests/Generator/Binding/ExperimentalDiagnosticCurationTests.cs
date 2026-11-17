using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// The <c>experimentalDiagnostic</c> group field: an operation is experimental by its route alone,
/// its group names the family's id, and every way the rows can disagree with the document or with
/// each other refuses generation. A type is experimental only when no stable operation reaches it.
/// </summary>
[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class ExperimentalDiagnosticCurationTests(PinnedBinding pinned)
{
    private static BindingException RefuseMergedGroups(SpecDocument document, IReadOnlyDictionary<string, GroupCuration> groups) =>
        Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document,
            Selection("probe.get", "other.get"), Curation(groups)));

    [Test]
    [Arguments("OPENCODE001", false)]
    [Arguments("OPENCODE002", true)]
    public async Task Bind_Should_Share_A_Raw_Childs_Diagnostic_Only_With_The_Nearest_Parent(string diagnostic, bool accepted)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("experimental.get", path: "/api/experimental/root", configure: static operation => operation.WithoutResponse(200).Response(204))
            .WithOperation("experimental.persistentPty.get", path: "/api/experimental/parent", configure: static operation => operation.WithoutResponse(200).Response(204))
            .WithOperation("experimental.persistentPty.child.get", path: "/api/experimental/child", configure: static operation => operation.WithoutResponse(200).Response(204))));
        var groups = Groups("experimental", ClientGroup("Experimental", null, null, experimentalDiagnostic: "OPENCODE001"));
        groups["experimental.persistentPty"] = ClientGroup("ParentProbes", null, null, experimentalDiagnostic: "OPENCODE002");
        groups["experimental.persistentPty.child"] = ClientGroup("Children", null, null, emission: EmissionMode.InternalRaw,
            experimentalDiagnostic: diagnostic) with
        { RootAccessor = false };
        var selection = Selection("experimental.get", "experimental.persistentPty.get", "experimental.persistentPty.child.get");
        if (accepted)
        {
            var plan = new BindingTestHost().Bind(document, selection, Curation(groups));
            await Assert.That(plan.Clients.Single(static client => client.Name == "ChildrenRawClient").Operations.Single().ExperimentalDiagnostic)
                .IsEqualTo("OPENCODE002");
            return;
        }

        var failure = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, selection, Curation(groups)));
        await Assert.That(failure.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Curation, "OPENCODE001",
                "experimentalDiagnostic 'OPENCODE001' is shared by groups that are not one merged family"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("other", false, true)]
    [Arguments("probe.child", true, true)]
    [Arguments("probe.child", false, false)]
    public async Task Bind_Should_Refuse_Diagnostic_Sharing_Outside_Accessor_Free_Raw_Subgroups(string group, bool accessor, bool internalRaw)
    {
        var emission = internalRaw ? EmissionMode.InternalRaw : EmissionMode.Public;
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("probe.get", path: "/api/experimental/probe", configure: static operation => operation.WithoutResponse(200).Response(204))
            .WithOperation(group + ".get", path: "/api/experimental/" + group.Replace('.', '/'), configure: static operation => operation.WithoutResponse(200).Response(204))));
        var groups = Groups("probe", ClientGroup("Probes", null, null, experimentalDiagnostic: "OPENCODE001"));
        groups[group] = ClientGroup("Children", null, null, emission: emission, experimentalDiagnostic: "OPENCODE001") with { RootAccessor = accessor };
        var failure = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("probe.get", group + ".get"), Curation(groups)));
        var expected = new List<BindingError>();
        if (emission == EmissionMode.Public)
        {
            expected.Add(new BindingError(BindingErrorCategory.Curation, group, "rootAccessor false requires internalRaw emission"));
        }

        expected.Add(new BindingError(BindingErrorCategory.Curation, "OPENCODE001",
            "experimentalDiagnostic 'OPENCODE001' is shared by groups that are not one merged family"));
        await Assert.That(failure.Errors).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("/api/probe")]
    [Arguments("/api/experimental")]
    [Arguments("/api/experimental-other/probe")]
    public async Task Bind_Should_Ignore_An_Experimental_Operation_Name_On_A_Stable_Route(string route)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("experimental.get", path: route, configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Type("object")))));

        var plan = new BindingTestHost().Bind(document, Selection("experimental.get"), Curation(Groups("experimental", RootGroup())));

        await Assert.That(plan.Clients.Single().Operations.Single().ExperimentalDiagnostic).IsNull();
    }

    [Test]
    public async Task Bind_Should_Keep_A_Union_Stable_When_A_Stable_Operation_Reaches_Its_Arms()
    {
        var plan = pinned.Plan;

        await Assert.That(plan.Unions.Single(static union => union.Name == "ISessionLogItem").ExperimentalDiagnostic).IsNull();
        await Assert.That(plan.HoistedInterfaces.Single(static value => value.Name == "IDurableEnvelope").ExperimentalDiagnostic).IsNull();
        await Assert.That(plan.Clients.SelectMany(static client => client.Operations).Single(static operation => operation.OperationId == "experimental.session.log")
            .ExperimentalDiagnostic).IsEqualTo("OPENCODE001");
    }

    [Test]
    [Arguments("/api/probe", true)]
    [Arguments("/api/experimental/probe", false)]
    public async Task Bind_Should_Refuse_A_Diagnostic_Without_A_Selected_Experimental_Route(string route, bool selected)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("probe.get", path: route, configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Type("object")))));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document,
            selected ? Selection("probe.get") : Selection(),
            Curation(Groups("probe", RootGroup() with { ExperimentalDiagnostic = "OPENCODE001" }))));

        await Assert.That(exception.Errors).Contains(static error => error.Problem == "experimentalDiagnostic requires a selected experimental route");
    }

    [Test]
    [Arguments("")]
    [Arguments("opencode001")]
    [Arguments("OPENCODE01")]
    [Arguments("OPENCODE0001")]
    [Arguments("OPENCODE00X")]
    [Arguments("OPENCODE001\n")]
    public async Task Bind_Should_Refuse_A_Malformed_Diagnostic_Id(string id)
    {
        var document = await BindingTestHost.IngestAsync(new ExperimentalOperationScenario());

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document,
            Selection("probe.get"), Curation(Groups("probe", RootGroup() with { ExperimentalDiagnostic = id }),
                stringQueryParameters: ExperimentalOperationScenario.QueryRows)));

        await Assert.That(exception.Errors).Contains(static error => error.Problem.Contains("must match ^OPENCODE[0-9]{3}$", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_Merged_Groups_With_Different_Diagnostics()
    {
        var document = await BindingTestHost.IngestAsync(TwoGroupsScenario());
        var groups = Groups("probe", ClientGroup("Probes", null, null, experimentalDiagnostic: "OPENCODE001"));
        groups["other"] = ClientGroup("Probes", null, null, experimentalDiagnostic: "OPENCODE002");

        var exception = RefuseMergedGroups(document, groups);

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Probes"
            && error.Problem.Contains("the same experimentalDiagnostic", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_An_All_Experimental_Root_Client_With_Different_Diagnostics()
    {
        var document = await BindingTestHost.IngestAsync(TwoGroupsScenario());
        var groups = Groups("probe", RootGroup() with { ExperimentalDiagnostic = "OPENCODE001" });
        groups["other"] = RootGroup() with { ExperimentalDiagnostic = "OPENCODE002" };

        var exception = RefuseMergedGroups(document, groups);

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "OpenCodeClient"
            && error.Problem.Contains("OPENCODE001, OPENCODE002", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_One_Diagnostic_Shared_By_Different_Families()
    {
        var document = await BindingTestHost.IngestAsync(TwoGroupsScenario());
        var groups = Groups("probe", ClientGroup("Probes", null, null, experimentalDiagnostic: "OPENCODE001"));
        groups["other"] = ClientGroup("Others", null, null, experimentalDiagnostic: "OPENCODE001");

        var exception = RefuseMergedGroups(document, groups);

        await Assert.That(exception.Errors).Contains(static error => error.Problem.Contains("not one merged family", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Allow_One_Diagnostic_Shared_By_A_Merged_Family()
    {
        var document = await BindingTestHost.IngestAsync(TwoGroupsScenario());
        var groups = Groups("probe", ClientGroup("Probes", null, null, experimentalDiagnostic: "OPENCODE001"));
        groups["other"] = groups["probe"];
        var curation = Curation(groups, operationNames: [OperationName("other.get", "GetOtherAsync")]);

        var plan = new BindingTestHost().Bind(document, Selection("probe.get", "other.get"), curation);

        await Assert.That(plan.Clients.Single(static client => client.Name == "ProbesClient").ExperimentalDiagnostic).IsEqualTo("OPENCODE001");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Mark_A_Model_Only_When_No_Stable_Operation_Reaches_It(bool stable)
    {
        var document = await BindingTestHost.IngestAsync(new ExperimentalOperationScenario(stable));
        var plan = new BindingTestHost().Bind(document,
            stable ? Selection("probe.get", "probe.list") : Selection("probe.get"),
            Curation(Groups("probe", RootGroup() with { ExperimentalDiagnostic = "OPENCODE001" }),
                stringQueryParameters: ExperimentalOperationScenario.QueryRows));

        await Assert.That(plan.Models.Single(static model => model.Name == "ProbeInfo").ExperimentalDiagnostic)
            .IsEqualTo(stable ? null : "OPENCODE001");
        await Assert.That(plan.Clients.Single().ExperimentalDiagnostic).IsEqualTo(stable ? null : "OPENCODE001");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Experimental_Only_Model_Reached_By_Two_Ids()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("ProbeInfo", static schema => schema.Type("object").Property("value", static value => value.Type("string")))
            .WithOperation("probe.get", path: "/api/experimental/probe", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("ProbeInfo")))
            .WithOperation("other.get", path: "/api/experimental/other", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("ProbeInfo")))));
        var groups = Groups("probe", ClientGroup("Probes", null, null, experimentalDiagnostic: "OPENCODE001"));
        groups["other"] = ClientGroup("Others", null, null, experimentalDiagnostic: "OPENCODE002");

        var exception = RefuseMergedGroups(document, groups);

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "ProbeInfo"
            && error.Problem.Contains("OPENCODE001, OPENCODE002", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Experimental_Route_Without_A_Diagnostic()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("probe.get", path: "/api/experimental/probe", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Type("object")))));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document,
            Selection("probe.get"), Curation(Groups("probe", RootGroup()))));

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "probe"
            && error.Problem.Contains("requires experimentalDiagnostic", StringComparison.Ordinal));
    }

    private static SpecScenario TwoGroupsScenario() => SpecScenario.Define(static spec => spec
        .WithOperation("probe.get", path: "/api/experimental/probe", configure: static operation => operation
            .Response(200, "application/json", static schema => schema.Type("object")))
        .WithOperation("other.get", path: "/api/experimental/other", configure: static operation => operation
            .Response(200, "application/json", static schema => schema.Type("object"))));
}
