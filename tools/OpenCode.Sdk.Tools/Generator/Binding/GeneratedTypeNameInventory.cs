using System.Collections.Frozen;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>The actual generated declarations, in collision-claim order, retaining their diagnostic owners.</summary>
internal sealed class GeneratedTypeNameInventory
{
    internal enum ClaimRole { Model, Client, Response, StreamAdapter, QueryRequest, EnvelopeDto, Hoisted, Derived }

    private readonly ILookup<string, GeneratedTypeNameClaim> _byName;

    private GeneratedTypeNameInventory(IReadOnlyList<GeneratedTypeNameClaim> claims)
    {
        Claims = Array.AsReadOnly([.. claims]);
        Names = claims.Select(static claim => claim.Name).ToFrozenSet(StringComparer.Ordinal);
        _byName = claims.ToLookup(static claim => claim.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<GeneratedTypeNameClaim> Claims { get; }

    public IReadOnlySet<string> Names { get; }

    public IEnumerable<GeneratedTypeNameClaim> OtherClaims(GeneratedTypeNameClaim claim) =>
        _byName[claim.Name].Where(other => !ReferenceEquals(other, claim));

    public static GeneratedTypeNameInventory Create(OwnershipCandidates candidates, IReadOnlyList<ClientPlan> clients,
        IReadOnlyList<GeneratedTypeNameClaim> hoistedClaims, IReadOnlyDictionary<string, string> schemaNames)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(hoistedClaims);
        ArgumentNullException.ThrowIfNull(schemaNames);
        var schemaOwners = schemaNames.ToLookup(static entry => entry.Value, static entry => entry.Key, StringComparer.Ordinal);
        var claims = schemaNames.Select(static entry => new GeneratedTypeNameClaim(entry.Value, entry.Key, ClaimRole.Model)).ToList();
        foreach (var name in candidates.Models.Select(static model => model.Name).Concat(candidates.Unions.Select(static union => union.Name))
                     .Where(name => !schemaOwners.Contains(name)))
        {
            claims.Add(new GeneratedTypeNameClaim(name, name, ClaimRole.Model));
        }
        claims.AddRange(candidates.Models.OfType<StructuralUnionModelPlan>()
            .Select(static model => new GeneratedTypeNameClaim(model.KindTypeName, model.Name, ClaimRole.Derived)));
        claims.AddRange(candidates.Unions.Select(static union => new GeneratedTypeNameClaim(union.UnknownTypeName, union.Name, ClaimRole.Derived)));
        claims.AddRange(clients.Select(static client => new GeneratedTypeNameClaim(client.Name, client.Name, ClaimRole.Client)));
        var operations = clients.SelectMany(static client => client.Operations).OrderBy(static operation => operation.OperationId, StringComparer.Ordinal).ToArray();
        claims.AddRange(operations.Where(static operation => operation.Envelope is not null)
            .Select(static operation => new GeneratedTypeNameClaim(operation.Envelope!.ResponseTypeName, operation.OperationId, ClaimRole.Response)));
        claims.AddRange(operations.Where(static operation => operation.Stream is not null)
            .Select(static operation => new GeneratedTypeNameClaim(operation.Stream!.AdapterTypeName, operation.OperationId, ClaimRole.StreamAdapter)));
        claims.AddRange(operations.Where(static operation => operation.QueryRequest is { RidesRequestBody: false })
            .Select(static operation => new GeneratedTypeNameClaim(operation.QueryRequest!.TypeName, operation.OperationId, ClaimRole.QueryRequest)));
        claims.AddRange(operations.Where(static operation => operation.Envelope is not null)
            .Select(static operation => new GeneratedTypeNameClaim(operation.Envelope!.AdapterTypeName, operation.OperationId, ClaimRole.Derived)));
        claims.AddRange(operations.Where(static operation => operation.Envelope?.EnvelopeDtoTypeName is not null)
            .Select(static operation => new GeneratedTypeNameClaim(operation.Envelope!.EnvelopeDtoTypeName!, operation.OperationId, ClaimRole.EnvelopeDto)));
        claims.AddRange(hoistedClaims);
        return new GeneratedTypeNameInventory(claims);
    }
}
