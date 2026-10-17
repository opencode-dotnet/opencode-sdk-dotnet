using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Generator.Binding.Support.OwnershipPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class GeneratedTypeNameInventoryTests
{
    [Test]
    public async Task Create_Should_Include_Every_Generated_Declaration_Without_Losing_Its_Owner()
    {
        var candidates = new OwnershipCandidates
        {
            Models = [Model("Body"), StructuralUnion("Shape", "ShapeKind")],
            Unions = [Union("Event")],
            HoistedInterfaces = [HoistedInterface("ICarrier"), HoistedInterface("INestedCarrier")],
        };
        var operation = BodyOperation("Widgets", "Body") with
        {
            QueryRequest = new QueryRequestPlan { TypeName = "Body", Properties = [], DerivesFromListRequest = false, RidesRequestBody = true },
            Envelope = new EnvelopePlan { ResponseTypeName = "Reply", AdapterTypeName = "ReplyAdapter", EnvelopeDtoTypeName = "ReplyDto", Kind = EnvelopeKind.Data, SuccessStatusCode = 200, PayloadName = "Item", PayloadType = UnionHoistPlanData.Named("Shape") },
        };
        var stream = operation with
        {
            OperationId = "Widgets.Watch",
            Envelope = null,
            QueryRequest = operation.QueryRequest with { TypeName = "Query", RidesRequestBody = false },
            Stream = new StreamPlan { AdapterTypeName = "StreamAdapter", PayloadTypeName = "Shape", FailureEventName = "error", CauseTypeName = "Shape[]" },
        };
        var client = new ClientPlan { Name = "Body", Namespace = GeneratedNamespace.Internal, Role = ClientRole.Collection, SubClients = [], Operations = [operation, stream] };
        var inventory = GeneratedTypeNameInventory.Create(candidates, [client], [
            new("ICarrier", "IEvent", GeneratedTypeNameInventory.ClaimRole.Hoisted, "data"),
            new("INestedCarrier", "ICarrier", GeneratedTypeNameInventory.ClaimRole.Hoisted, "nested"),
        ], new Dictionary<string, string>(StringComparer.Ordinal) { ["BodySchema"] = "Body" });
        await Assert.That(inventory.Claims.Select(static claim => (claim.Name, claim.Subject))).IsEquivalentTo([
            ("Body", "BodySchema"), ("Shape", "Shape"), ("IEvent", "IEvent"), ("ShapeKind", "Shape"), ("UnknownEvent", "IEvent"),
            ("Body", "Body"), ("Reply", "Widgets.SendBody"), ("StreamAdapter", "Widgets.Watch"), ("Query", "Widgets.Watch"),
            ("ReplyAdapter", "Widgets.SendBody"), ("ReplyDto", "Widgets.SendBody"), ("ICarrier", "IEvent"), ("INestedCarrier", "ICarrier"),
        ], CollectionOrdering.Matching);
        await Assert.That(inventory.Names.Order(StringComparer.Ordinal)).IsEquivalentTo([
            "Body", "ICarrier", "IEvent", "INestedCarrier", "Query", "Reply", "ReplyAdapter", "ReplyDto", "Shape", "ShapeKind", "StreamAdapter", "UnknownEvent",
        ], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(inventory.OtherClaims(inventory.Claims[0]).Select(static claim => claim.Subject))
            .IsEquivalentTo(["Body"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }
}
