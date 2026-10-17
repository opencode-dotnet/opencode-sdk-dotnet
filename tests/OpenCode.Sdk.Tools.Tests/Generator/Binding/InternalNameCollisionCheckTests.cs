using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Generator.Binding.Support.OwnershipPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// Every bound type name the check knows (models, structural kinds, unions and their unknown
/// carriers, hoisted interfaces, clients, query requests, responses, response adapters, envelope
/// DTOs and stream adapters) is refused as one naming error when it equals a hand-written internal
/// type name; ordinary names pass.
/// </summary>
public sealed class InternalNameCollisionCheckTests
{
    private const string ModelSlot = "model";
    private const string StructuralKindSlot = "structural kind";
    private const string UnionSlot = "union";
    private const string UnknownCarrierSlot = "unknown carrier";
    private const string HoistedSlot = "hoisted interface";
    private const string ClientSlot = "client";
    private const string QueryRequestSlot = "query request";
    private const string ResponseSlot = "response";
    private const string ResponseAdapterSlot = "response adapter";
    private const string EnvelopeDtoSlot = "envelope dto";
    private const string StreamAdapterSlot = "stream adapter";
    private const string Pipeline = "Pipeline";
    private const string PlanNamespace = "OpenCode.Sdk.Models";

    [Test]
    [Arguments(ModelSlot, Pipeline)]
    [Arguments(StructuralKindSlot, "FailurePhase")]
    [Arguments(UnionSlot, "IStreamAdapter")]
    [Arguments(UnknownCarrierSlot, "ServerSentEvent")]
    [Arguments(HoistedSlot, "ICursorPageAdapter")]
    [Arguments(ClientSlot, "TransportPolicy")]
    [Arguments(QueryRequestSlot, "DeclaredHeader")]
    [Arguments(ResponseSlot, "ResponseBody")]
    [Arguments(ResponseAdapterSlot, "ResponseAdapter")]
    [Arguments(EnvelopeDtoSlot, "EncodedResponseBody")]
    [Arguments(StreamAdapterSlot, "ServerSentEventReader")]
    public async Task Check_Should_Report_One_Naming_Error_Naming_It_When_A_Bound_Type_Takes_A_Reserved_Internal_Name(
        string slot, string reservedName)
    {
        var errors = Check(Rename(new PlanNames(), slot, reservedName));

        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);
        await Assert.That(exception.Errors).Count().IsEqualTo(1);
        var error = exception.Errors[0];
        await Assert.That(error.Category).IsEqualTo(BindingErrorCategory.Naming);
        await Assert.That(error.Subject).IsEqualTo(reservedName);
        await Assert.That(error.Problem).Contains(reservedName, StringComparison.Ordinal);
    }

    [Test]
    public async Task Check_Should_Report_Nothing_When_No_Bound_Type_Takes_A_Reserved_Internal_Name()
    {
        var errors = Check(new PlanNames());

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Check_Should_Report_A_Reserved_Name_Once_When_Several_Bound_Types_Take_It()
    {
        var names = Rename(Rename(new PlanNames(), ModelSlot, Pipeline), ClientSlot, Pipeline);

        var errors = Check(names);

        await Assert.That(errors.Count).IsEqualTo(1);
    }

    private static PlanNames Rename(PlanNames names, string slot, string name) =>
        slot switch
        {
            ModelSlot => names with { Model = name },
            StructuralKindSlot => names with { StructuralKind = name },
            UnionSlot => names with { Union = name },
            UnknownCarrierSlot => names with { UnknownCarrier = name },
            HoistedSlot => names with { Hoisted = name },
            ClientSlot => names with { Client = name },
            QueryRequestSlot => names with { QueryRequest = name },
            ResponseSlot => names with { Response = name },
            ResponseAdapterSlot => names with { ResponseAdapter = name },
            EnvelopeDtoSlot => names with { EnvelopeDto = name },
            StreamAdapterSlot => names with { StreamAdapter = name },
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "The scenario names no such plan slot."),
        };

    private static BindingErrorCollector Check(PlanNames names)
    {
        var errors = new BindingErrorCollector();
        var candidates = new OwnershipCandidates
        {
            Models = [Model(names.Model), StructuralUnion(names.StructuralUnion, names.StructuralKind)],
            Unions = [Union(names.UnionConcept) with { Name = names.Union, UnknownTypeName = names.UnknownCarrier }],
            HoistedInterfaces =
            [
                new HoistedInterfacePlan
                {
                    Name = names.Hoisted,
                    Namespace = PlanNamespace,
                    ImplementedBy = [names.Model],
                    Members = [],
                },
            ],
        };
        var client = new ClientPlan
        {
            Name = names.Client,
            Namespace = "OpenCode.Sdk",
            Role = ClientRole.Root,
            SubClients = [],
            Operations = [ListOperation(names), StreamOperation(names)],
        };

        InternalNameCollisionCheck.Check(candidates, [client], errors);
        return errors;
    }

    private static OperationPlan ListOperation(PlanNames names) =>
        Operation(names, "List") with
        {
            QueryRequest = new QueryRequestPlan
            {
                TypeName = names.QueryRequest,
                DerivesFromListRequest = false,
                Properties = [],
            },
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = names.Response,
                AdapterTypeName = names.ResponseAdapter,
                PayloadName = "Item",
                PayloadType = UnionHoistPlanData.Named(names.Model),
                Kind = EnvelopeKind.Data,
                SuccessStatusCode = 200,
                EnvelopeDtoTypeName = names.EnvelopeDto,
            },
        };

    private static OperationPlan StreamOperation(PlanNames names) =>
        Operation(names, "Subscribe") with
        {
            Stream = new StreamPlan
            {
                PayloadTypeName = names.Model,
                AdapterTypeName = names.StreamAdapter,
                FailureEventName = "error",
                CauseTypeName = "ExampleCause",
            },
        };

    private static OperationPlan Operation(PlanNames names, string memberName) =>
        new()
        {
            OperationId = $"example.{memberName}",
            MethodName = $"{memberName}Async",
            HttpMethod = "get",
            RouteTemplate = $"/example/{memberName}",
            RouteContainerName = names.Client,
            RouteMemberName = memberName,
            Parameters = [],
            ErrorMap = new ErrorMapPlan { Statuses = [] },
        };

    /// <summary>The bound type names one scenario carries; the defaults collide with no internal type.</summary>
    private sealed record PlanNames
    {
        public string Model { get; init; } = "ExampleItem";

        public string StructuralUnion { get; init; } = "ExampleShape";

        public string StructuralKind { get; init; } = "ExampleShapeKind";

        public string UnionConcept { get; init; } = "ExampleEvent";

        public string Union { get; init; } = "IExampleEvent";

        public string UnknownCarrier { get; init; } = "UnknownExampleEvent";

        public string Hoisted { get; init; } = "IExampleNamed";

        public string Client { get; init; } = "ExamplesClient";

        public string QueryRequest { get; init; } = "ExampleListRequest";

        public string Response { get; init; } = "ExampleListResponse";

        public string ResponseAdapter { get; init; } = "ExampleListResponseAdapter";

        public string EnvelopeDto { get; init; } = "ExampleListEnvelope";

        public string StreamAdapter { get; init; } = "ExampleSubscribeStreamAdapter";
    }
}
