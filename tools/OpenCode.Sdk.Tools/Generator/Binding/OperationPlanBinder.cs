using System.Diagnostics;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class OperationPlanBinder
{
    private const string RootClientName = "OpenCodeClient";

    private readonly StringComparer _comparer = StringComparer.Ordinal;

    public IReadOnlyList<ClientPlan> Bind(SpecDocument document, IReadOnlyList<SpecOperation> selected,
        GenerationCuration curation, SchemaBindingResult schemas, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(errors);

        var typeNames = schemas.TypeNames;
        var typeBinder = new TypePlanBinder(document.Schemas, typeNames, errors);
        var bound = new List<BoundOperation>(selected.Count);
        foreach (var operation in selected.OrderBy(static operation => operation.OperationId, _comparer))
        {
            var operationPlan = new SingleOperationBinder(new OperationFacetContext(document, operation, curation, typeNames, errors, typeBinder), schemas.ErrorUnion).Bind();
            if (operationPlan is not null)
            {
                bound.Add(operationPlan);
            }
        }

        if (bound.Count is 0)
        {
            return [];
        }

        var clients = ExperimentalClientPolicy.Apply(AssembleClients(bound), errors);
        CheckMemberCollisions(clients, errors);
        return clients;
    }

    private List<ClientPlan> AssembleClients(List<BoundOperation> bound)
    {
        var rootOperations = bound
            .Where(static operation => operation.Row.Placement is GroupPlacement.Root)
            .Select(static operation => operation.Plan)
            .OrderBy(static plan => plan.MethodName, _comparer)
            .ToArray();
        var subClients = new List<ClientReferencePlan>();
        var clients = new List<ClientPlan>();

        // Wire groups sharing a curated client name merge into one client family; the
        // curation validator has already demanded identical handle declarations.
        foreach (var group in bound
                     .Where(static operation => operation.Row.Placement is GroupPlacement.Client)
                     .GroupBy(static operation => operation.Row.ClientName!, _comparer)
                     .OrderBy(static group => group.Key, _comparer))
        {
            var members = group.ToArray();
            var row = members[0].Row;
            var collectionName = $"{row.ClientName}Client";
            subClients.Add(new ClientReferencePlan
            {
                PropertyName = row.ClientName!,
                TypeName = collectionName,
            });
            clients.AddRange(CreateGroupClients(members, row, collectionName));
        }

        clients.Add(new ClientPlan
        {
            Name = RootClientName,
            Namespace = GeneratedNamespace.Public,
            Role = ClientRole.Root,
            SubClients = [.. subClients.OrderBy(static reference => reference.PropertyName, _comparer)],
            Operations = rootOperations,
        });

        return
        [
            .. clients
                .OrderBy(static client => client.Role is ClientRole.Root ? 0 : 1)
                .ThenBy(static client => client.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>An internal-raw family's clients are internal types, so they declare the internal namespace.</summary>
    private static string ClientNamespace(EmissionMode emission) =>
        emission is EmissionMode.InternalRaw ? GeneratedNamespace.Internal : GeneratedNamespace.Public;

    /// <summary>
    /// Builds a family's clients. An internal-raw family takes raw type names throughout
    /// (ADR-0021): the public family name belongs to the hand-written door the root client
    /// keeps pointing at, so nothing generated may claim it.
    /// </summary>
    private IEnumerable<ClientPlan> CreateGroupClients(IReadOnlyList<BoundOperation> group, GroupCuration row, string collectionName)
    {
        var handleOperations = group.Where(static operation => operation.IsHandleOperation).ToArray();
        var collectionOperations = group
            .Where(static operation => !operation.IsHandleOperation)
            .Select(static operation => operation.Plan)
            .OrderBy(static plan => plan.MethodName, _comparer)
            .ToArray();
        var handleParameter = handleOperations
            .OrderBy(static operation => operation.Plan.MethodName, _comparer)
            .SelectMany(static operation => operation.Plan.Parameters)
            .FirstOrDefault(static parameter => parameter.IsHandleParameter);
        var collectionTypeName = ClientTypeName(collectionName, row.Emission);
        var handleTypeName = row.HandleName is null ? null : ClientTypeName(row.HandleName, row.Emission);

        yield return new ClientPlan
        {
            Name = collectionTypeName,
            Namespace = ClientNamespace(row.Emission),
            Role = ClientRole.Collection,
            Emission = row.Emission,
            ContainerName = row.ClientName,
            SubClients = [],
            HandleFactory = handleParameter is null
                ? null
                : new HandleFactoryPlan
                {
                    MethodName = $"Get{handleTypeName}",
                    HandleTypeName = handleTypeName!,
                    Parameter = handleParameter,
                },
            Operations = collectionOperations,
        };

        if (handleParameter is not null)
        {
            yield return new ClientPlan
            {
                Name = handleTypeName!,
                Namespace = ClientNamespace(row.Emission),
                Role = ClientRole.Handle,
                Emission = row.Emission,
                ContainerName = row.ClientName,
                SubClients = [],
                HandleParameter = handleParameter,
                Operations =
                [
                    .. handleOperations
                        .Select(static operation => operation.Plan)
                        .OrderBy(static plan => plan.MethodName, _comparer),
                ],
            };
        }
    }

    private static string ClientTypeName(string familyName, EmissionMode emission) =>
        emission is EmissionMode.InternalRaw ? CSharpNamePolicy.ToRawClientName(familyName) : familyName;

    private void CheckMemberCollisions(List<ClientPlan> clients, BindingErrorCollector errors)
    {
        foreach (var client in clients)
        {
            var members = client
                .Operations.Select(static operation => operation.MethodName)
                .Concat(client.Operations
                    .Where(static operation => operation.Pagination is not null)
                    .Select(static operation => operation.Pagination!.MethodName))
                .Concat(client.SubClients.Select(static reference => reference.PropertyName));
            if (client.HandleFactory is not null)
            {
                members = members.Append(client.HandleFactory.MethodName);
            }

            foreach (var member in members
                         .GroupBy(static member => member, _comparer)
                         .Where(static member => member.Skip(1).Any())
                         .OrderBy(static member => member.Key, _comparer))
            {
                errors.Add(BindingErrorCategory.Naming, client.Name, $"multiple members map to C# name '{member.Key}'");
            }
        }
    }

    public IReadOnlySet<string> CheckTypeNameCollisions(GeneratedTypeNameInventory inventory, IReadOnlyList<ClientPlan> clients, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(errors);
        var refusedHoists = new HashSet<string>(_comparer);
        var hoistedOwners = new HashSet<string>(_comparer);
        foreach (var claim in inventory.Claims.Where(claim => claim.Role is GeneratedTypeNameInventory.ClaimRole.Hoisted
                     && (!hoistedOwners.Add(claim.Name) || inventory.OtherClaims(claim).Any(static other => other.Role is not GeneratedTypeNameInventory.ClaimRole.Hoisted))))
        {
            if (inventory.OtherClaims(claim).Any(static other => other.Role is not GeneratedTypeNameInventory.ClaimRole.Hoisted))
            {
                _ = refusedHoists.Add(claim.Name);
            }
            errors.Add(BindingErrorCategory.Naming, claim.Subject,
                $"hoisted member '{claim.WireName}' resolves to '{claim.Name}', which already names another generated type");
        }

        // Spine names claim first, followed by models individually so a model/spine duplicate refuses.
        var owners = new HashSet<string>(_comparer);
        owners.UnionWith(ReservedNamePolicy.SpineTypeNames);
        // Derived kit/adapter names reserve their declarations; their owning declaration reports duplicates.
        owners.UnionWith(inventory.Claims.Where(static claim => claim.Role is GeneratedTypeNameInventory.ClaimRole.Derived).Select(static claim => claim.Name));
        foreach (var claim in inventory.Claims.Where(claim => claim.Role is GeneratedTypeNameInventory.ClaimRole.Model
                     && !owners.Add(claim.Name))
                     .OrderBy(static claim => claim.Subject, StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Naming, claim.Subject, ReservedNamePolicy.SpineTypeNames.Contains(claim.Name)
                ? $"model type name '{claim.Name}' collides with the hand-written spine type '{claim.Name}'"
                : $"type name '{claim.Name}' collides with another generated type");
        }

        foreach (var claim in inventory.Claims.Where(claim => claim.Role is not (GeneratedTypeNameInventory.ClaimRole.Model
                     or GeneratedTypeNameInventory.ClaimRole.Hoisted or GeneratedTypeNameInventory.ClaimRole.EnvelopeDto
                     or GeneratedTypeNameInventory.ClaimRole.Derived) && !owners.Add(claim.Name)))
        {
            var problem = claim.Role switch
            {
                GeneratedTypeNameInventory.ClaimRole.Client => $"client type name '{claim.Name}' collides with another generated type",
                GeneratedTypeNameInventory.ClaimRole.Response => $"multiple operations map to response type '{claim.Name}'",
                GeneratedTypeNameInventory.ClaimRole.StreamAdapter => $"multiple operations map to stream adapter '{claim.Name}'",
                GeneratedTypeNameInventory.ClaimRole.QueryRequest => $"request type name '{claim.Name}' collides with another generated type",
                GeneratedTypeNameInventory.ClaimRole.Model or GeneratedTypeNameInventory.ClaimRole.EnvelopeDto
                    or GeneratedTypeNameInventory.ClaimRole.Hoisted or GeneratedTypeNameInventory.ClaimRole.Derived => throw new UnreachableException(),
                _ => throw new UnreachableException(),
            };
            errors.Add(BindingErrorCategory.Naming, claim.Subject, problem);
        }

        var derivedOwners = new HashSet<string>(ReservedNamePolicy.SpineTypeNames, _comparer);
        var responseNames = inventory.Claims.Where(static claim => claim.Role is GeneratedTypeNameInventory.ClaimRole.Response)
            .ToDictionary(static claim => claim.Subject, static claim => claim.Name, _comparer);
        foreach (var claim in inventory.Claims.Where(claim => claim.Role is GeneratedTypeNameInventory.ClaimRole.Derived && !derivedOwners.Add(claim.Name)))
        {
            // Equal response declarations already refuse with their response-name diagnostic.
            if (responseNames.TryGetValue(claim.Subject, out var responseName)
                && inventory.OtherClaims(claim).Any(other => other.Role is GeneratedTypeNameInventory.ClaimRole.Derived
                    && responseNames.TryGetValue(other.Subject, out var otherResponse) && string.Equals(responseName, otherResponse, StringComparison.Ordinal)))
            {
                continue;
            }
            errors.Add(BindingErrorCategory.Naming, claim.Subject, $"type name '{claim.Name}' collides with another generated type");
        }

        var routeMembers = new HashSet<string>(_comparer);
        var operations = clients.SelectMany(static client => client.Operations).OrderBy(static operation => operation.OperationId, _comparer).ToArray();
        foreach (var operation in operations.Where(operation => !routeMembers.Add($"{operation.RouteContainerName}.{operation.RouteMemberName}")))
        {
            errors.Add(BindingErrorCategory.Naming, operation.OperationId,
                $"multiple operations map to route member '{operation.RouteContainerName}.{operation.RouteMemberName}'");
        }
        return refusedHoists;
    }
}
