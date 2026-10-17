using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class UnionMemberHoistBinderTests
{
    [Test]
    [Arguments(null, "curation is null")]
    [Arguments("Owner", "curation names no owner")]
    [Arguments("Property", "curation names no property")]
    public async Task Bind_Should_Refuse_Unsafe_Hoisted_Name_Rows(string? blankField, string problem)
    {
        var exception = await BindUnsafeContractRowAsync("HoistedMemberNames", blankField);

        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation,
            "hoistedMemberNames[0]", "hoistedMemberNames[0] " + problem));
    }

    [Test]
    [Arguments(false, "hoisted member name curation must declare a reason")]
    [Arguments(true, "hoisted member name curation is duplicated")]
    [Arguments(false, "hoisted member name curation for 'absent' is unused: the interface hoists no promoted object under that property", "unused", BindingErrorCategory.Curation, "IExampleEvent")]
    [Arguments(false, "hoisted member 'durable' resolves to 'CreatedEventDurable', which already names another generated type", "collision", BindingErrorCategory.Naming, "IExampleEvent")]
    [Arguments(false, "hoisted member 'durable' shares its records with 'IDurableEnvelope' and cannot also be named 'IOtherEnvelope'", "shared", BindingErrorCategory.Naming, "ISecondEvent")]
    public async Task Bind_Should_Refuse_Invalid_Hoisted_Name_Rows(bool duplicate, string problem, string fault = "contract",
        BindingErrorCategory category = BindingErrorCategory.Curation, string subject = "IExampleEvent")
    {
        var row = HoistedMemberName("IExampleEvent", "durable", "IDurableEnvelope", reason: duplicate ? "Name the carrier." : " ");
        if (fault != "contract")
        {
            row = row with
            {
                Reason = "Name the carrier.",
                Property = fault == "unused" ? "absent" : "durable",
                DotNetName = fault == "collision" ? "CreatedEventDurable" : "IDurableEnvelope",
            };
        }
        var curation = Curation(Groups(), hoistedMemberNames: duplicate ? [row, row] : [row]);
        if (fault == "shared")
        {
            curation = curation with { HoistedMemberNames = [row, HoistedMemberName("ISecondEvent", "durable", "IOtherEnvelope")] };
        }

        var exception = Assert.Throws<BindingException>(() => _ = fault == "shared" ? Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent")), Union("ISecondEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Envelope("CreatedEventDurable", "1"), Envelope("DeletedEventDurable", "2")], curation) : HoistDurableCarrier(curation));

        if (fault == "contract")
        {
            await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(category, subject, problem));
        }
        else
        {
            await Assert.That(exception.Errors.Single(error => error.Category == category && error.Subject == subject))
                .IsEqualTo(new BindingError(category, subject, problem));
        }
    }

    [Test]
    public async Task Bind_Should_Hoist_A_Member_Every_Variant_Declares_Identically()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("id", Named("string"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("id", Named("string"), isRequired: true)),
            ]);

        var union = plan.Unions.Single();
        await Assert.That(union.HoistedMembers.Select(static member => member.Name)).IsEquivalentTo(["Id"]);
        await Assert.That(union.HoistedMembers.Single().WireName).IsEqualTo("id");
    }

    [Test]
    public async Task Bind_Should_Hoist_A_Member_Whose_Non_Dispatch_Literal_Values_Differ()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", NumberLiteral("version", "1")),
                Variant("DeletedEvent", "deleted", NumberLiteral("version", "2")),
            ]);

        await Assert.That(plan.Unions.Single().HoistedMembers.Select(static member => member.Name)).IsEquivalentTo(["Version"]);
    }

    [Test]
    public async Task Bind_Should_Declare_A_Hoisted_Union_Member_Nullable_Because_The_Unknown_Carrier_Answers_Null()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("seq", Named("long"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("seq", Named("long"), isRequired: true)),
            ]);

        await Assert.That(plan.Unions.Single().HoistedMembers.Single().Type.IsNullable).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Not_Hoist_The_Union_Discriminator()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [Variant("CreatedEvent", "created"), Variant("DeletedEvent", "deleted")]);

        await Assert.That(plan.Unions.Single().HoistedMembers).IsEmpty();
    }

    [Test]
    public async Task Bind_Should_Not_Hoist_A_Member_One_Variant_Omits()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("id", Named("string"), isRequired: true)),
                Variant("DeletedEvent", "deleted"),
            ]);

        await Assert.That(plan.Unions.Single().HoistedMembers).IsEmpty();
    }

    [Test]
    public async Task Bind_Should_Not_Hoist_A_Member_The_Variants_Require_Differently()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("id", Named("string"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("id", Named("string", isNullable: true), isRequired: false)),
            ]);

        await Assert.That(plan.Unions.Single().HoistedMembers).IsEmpty();
    }

    [Test]
    public async Task Bind_Should_Not_Hoist_A_Member_The_Variants_Type_Differently()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("id", Named("string"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("id", Named("long"), isRequired: true)),
            ]);

        await Assert.That(plan.Unions.Single().HoistedMembers).IsEmpty();
    }

    [Test]
    public async Task Bind_Should_Hoist_Structurally_Identical_Promoted_Objects_Behind_One_Interface()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Envelope("CreatedEventDurable", "1"),
                Envelope("DeletedEventDurable", "2"),
            ]);

        var hoisted = plan.Interfaces.Single();
        await Assert.That(hoisted.Name).IsEqualTo("IExampleEventDurable");
        await Assert.That(hoisted.Members.Select(static member => member.Name)).IsEquivalentTo(["Seq", "Version"]);
        // No carrier implements a hoisted interface, so its members stay as the records declare them.
        await Assert.That(hoisted.Members.All(static member => !member.Type.IsNullable)).IsTrue();
        await Assert.That(Model(plan, "CreatedEventDurable").ImplementedHoistedInterfaceNames).IsEquivalentTo(["IExampleEventDurable"]);
        await Assert.That(Model(plan, "DeletedEventDurable").ImplementedHoistedInterfaceNames).IsEquivalentTo(["IExampleEventDurable"]);

        var member = plan.Unions.Single().HoistedMembers.Single();
        await Assert.That(((NamedTypeReferencePlan)member.Type).Name).IsEqualTo("IExampleEventDurable");
        await Assert.That(member.Type.IsNullable).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Hoist_A_Promoted_Object_Inside_A_Promoted_Object()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Record("CreatedEventDurable", Property("origin", Named("CreatedEventDurableOrigin"), isRequired: true)),
                Record("DeletedEventDurable", Property("origin", Named("DeletedEventDurableOrigin"), isRequired: true)),
                Record("CreatedEventDurableOrigin", NumberLiteral("version", "1")),
                Record("DeletedEventDurableOrigin", NumberLiteral("version", "2")),
            ]);

        await Assert.That(plan.Interfaces.Select(static hoisted => hoisted.Name).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["IExampleEventDurable", "IExampleEventDurableOrigin"]);
        var envelope = plan.Interfaces.Single(static hoisted => hoisted.Name == "IExampleEventDurable");
        await Assert.That(((NamedTypeReferencePlan)envelope.Members.Single().Type).Name).IsEqualTo("IExampleEventDurableOrigin");
        await Assert.That(Model(plan, "CreatedEventDurable").ExplicitHoistedImplementations.Single().MemberName).IsEqualTo("Origin");
    }

    [Test]
    public async Task Bind_Should_Give_A_Variant_An_Explicit_Implementation_Only_When_Its_Own_Property_Cannot_Satisfy_The_Interface()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created",
                    Property("id", Named("string"), isRequired: true),
                    Property("seq", Named("long"), isRequired: true)),
                Variant("DeletedEvent", "deleted",
                    Property("id", Named("string"), isRequired: true),
                    Property("seq", Named("long"), isRequired: true)),
            ]);

        var created = Model(plan, "CreatedEvent");
        // A reference type already answers its nullable annotation; a value type needs the cast.
        await Assert.That(created.ExplicitHoistedImplementations.Select(static implementation => implementation.MemberName))
            .IsEquivalentTo(["Seq"]);
        await Assert.That(created.ExplicitHoistedImplementations.Single().InterfaceName).IsEqualTo("IExampleEvent");
    }

    [Test]
    public async Task Bind_Should_Not_Redeclare_A_Member_The_Outer_Union_Already_Hoisted()
    {
        var outer = Union("IExampleEvent", Arm("CreatedEvent"), NestedArm("IExamplePhase"));
        var nested = Union("IExamplePhase", Arm("PhaseStartedEvent"), Arm("PhaseEndedEvent")) with
        {
            MarkerWireName = "status",
            MarkerName = "Status",
            BaseTypeName = "IExampleEvent",
        };

        var plan = Hoist(
            [outer, nested],
            [
                Variant("CreatedEvent", "created", Property("id", Named("string"), isRequired: true)),
                Variant("PhaseStartedEvent", "started", Property("id", Named("string"), isRequired: true),
                    Property("phase", Named("string"), isRequired: true)),
                Variant("PhaseEndedEvent", "ended", Property("id", Named("string"), isRequired: true),
                    Property("phase", Named("string"), isRequired: true)),
            ]);

        await Assert.That(plan.Unions.Single(static union => union.Name == "IExampleEvent").HoistedMembers
            .Select(static member => member.Name)).IsEquivalentTo(["Id"]);
        await Assert.That(plan.Unions.Single(static union => union.Name == "IExamplePhase").HoistedMembers
            .Select(static member => member.Name)).IsEquivalentTo(["Phase"]);
    }

    [Test]
    public async Task Bind_Should_Let_A_Reasoned_Curation_Row_Name_A_Hoisted_Interface()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Envelope("CreatedEventDurable", "1"),
                Envelope("DeletedEventDurable", "2"),
            ],
            Curation(Groups(), hoistedMemberNames: [HoistedMemberName("IExampleEvent", "durable", "IDurableEnvelope")]));

        await Assert.That(plan.Interfaces.Single().Name).IsEqualTo("IDurableEnvelope");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Hoisted_Name_Row_No_Hoist_Answers()
    {
        var exception = Assert.Throws<BindingException>(() => Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [Variant("CreatedEvent", "created"), Variant("DeletedEvent", "deleted")],
            Curation(Groups(), hoistedMemberNames: [HoistedMemberName("IExampleEvent", "durable", "IDurableEnvelope")])));

        await Assert.That(exception.Errors.Single().Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(exception.Errors.Single().Problem).Contains("hoists no promoted object");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Hoisted_Interface_Name_Another_Type_Already_Owns()
    {
        var exception = Assert.Throws<BindingException>(() => Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Envelope("CreatedEventDurable", "1"),
                Envelope("DeletedEventDurable", "2"),
            ],
            Curation(Groups(), hoistedMemberNames: [HoistedMemberName("IExampleEvent", "durable", "CreatedEventDurable")])));

        await Assert.That(exception.Errors.Single().Category).IsEqualTo(BindingErrorCategory.Naming);
        await Assert.That(exception.Errors.Single().Problem).Contains("already names");
    }

    /// <summary>
    /// A carrier interface declares the unwrapped shape of every member, including one the records
    /// carry as the tri-state wrapper, so the records answer that member explicitly through its
    /// value. Without this the carrier would declare <c>string?</c> against records declaring
    /// <c>Optional&lt;string?&gt;</c> and the generated tree would not compile.
    /// </summary>
    [Test]
    public async Task Bind_Should_Answer_A_Tristate_Carrier_Member_Through_Its_Value()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Record("CreatedEventDurable", TristateNote()),
                Record("DeletedEventDurable", TristateNote()),
            ]);

        var carrier = plan.Interfaces.Single();
        var member = carrier.Members.Single();
        await Assert.That(member.Name).IsEqualTo("Note");
        await Assert.That(member.Type).IsEqualTo(Named("string", isNullable: true));

        foreach (var recordName in new[] { "CreatedEventDurable", "DeletedEventDurable", })
        {
            var implementation = Model(plan, recordName).ExplicitHoistedImplementations.Single();
            await Assert.That(implementation.InterfaceName).IsEqualTo(carrier.Name);
            await Assert.That(implementation.MemberName).IsEqualTo("Note");
            await Assert.That(implementation.PropertyName).IsEqualTo("Note");
            await Assert.That(implementation.ReadsOptionalValue).IsTrue();
        }
    }

    [Test]
    public async Task Bind_Should_Not_Answer_A_Plain_Carrier_Member_Explicitly()
    {
        var plan = Hoist(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Envelope("CreatedEventDurable", "1"),
                Envelope("DeletedEventDurable", "2"),
            ]);

        // The records declare Seq and Version exactly as the carrier does, so nothing is explicit.
        await Assert.That(Model(plan, "CreatedEventDurable").ExplicitHoistedImplementations).IsEmpty();
        await Assert.That(Model(plan, "DeletedEventDurable").ExplicitHoistedImplementations).IsEmpty();
    }

    private static ModelPropertyPlan TristateNote() =>
        Property("note", Named("string", isNullable: true), isRequired: false, emitsOptionalWrapper: true);

    private static ObjectModelPlan Model(UnionHoistPlan plan, string name) =>
        plan.Models.OfType<ObjectModelPlan>().Single(model => model.Name == name);

    private static UnionHoistPlan Hoist(IReadOnlyList<UnionPlan> unions, IReadOnlyList<ModelPlan> models,
        GenerationCuration? curation = null)
    {
        var errors = new BindingErrorCollector();
        var plan = new UnionMemberHoistBinder().Bind(unions, models, curation ?? Curation(Groups()), errors);
        errors.ThrowIfAny();
        return plan;
    }
}
