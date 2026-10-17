using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Builds the interface a hoisted promoted-object member is declared with, and records what the
/// promoted records gain from it. The records are not collapsed: one wire schema stays one
/// record (ADR-0004), and the interface is the CLR type the union interface can name for all of
/// them. Its members are not nullable — every implementer is a real record, so there is no
/// carrier to answer null.
/// </summary>
internal sealed class HoistedCarrierComposer(
    IReadOnlyDictionary<string, ObjectModelPlan> models,
    IReadOnlyList<HoistedMemberNameCuration> namingRows,
    HoistedMemberSatisfactionPolicy satisfaction,
    HoistedImplementationAccumulator implementations,
    BindingErrorCollector errors)
{
    private readonly BindingErrorCollector _errors = errors ?? throw new ArgumentNullException(nameof(errors));

    private readonly HoistedMemberSatisfactionPolicy _satisfaction = satisfaction ?? throw new ArgumentNullException(nameof(satisfaction));

    private readonly HoistedImplementationAccumulator _implementations =
        implementations ?? throw new ArgumentNullException(nameof(implementations));

    private readonly IReadOnlyDictionary<string, ObjectModelPlan> _models = models ?? throw new ArgumentNullException(nameof(models));

    private readonly IReadOnlyList<HoistedMemberNameCuration> _namingRows =
        CurationRowContract.Validate(nameof(GenerationCuration.HoistedMemberNames),
            namingRows ?? throw new ArgumentNullException(nameof(namingRows)), errors) ? namingRows : [];

    private readonly List<GeneratedTypeNameClaim> _claims = [];

    public IReadOnlyList<GeneratedTypeNameClaim> Claims => _claims.AsReadOnly();

    private readonly Dictionary<string, string> _byRecordSet = new(StringComparer.Ordinal);

    private readonly Dictionary<string, HoistedInterfacePlan> _interfaces = new(StringComparer.Ordinal);

    private readonly HashSet<string> _usedRows = new(StringComparer.Ordinal);

    public IReadOnlyList<HoistedInterfacePlan> Interfaces =>
        Array.AsReadOnly([.. _interfaces.Values.OrderBy(static plan => plan.Name, StringComparer.Ordinal)]);

    /// <summary>Returns the interface the records answer to, or null when the name refuses.</summary>
    public string? Resolve(string owner, string ownerConcept, ModelPropertyPlan property, IReadOnlyList<string> recordNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConcept);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(recordNames);

        var curated = CuratedName(owner, property.WireName);
        var key = string.Join('\0', recordNames);
        if (_byRecordSet.TryGetValue(key, out var existing))
        {
            ReportCuratedCollision(owner, property, curated, existing);
            return existing;
        }

        var name = curated ?? string.Concat("I", ownerConcept, property.Name);
        _claims.Add(new GeneratedTypeNameClaim(name, owner, GeneratedTypeNameInventory.ClaimRole.Hoisted, property.WireName));
        var plan = new HoistedInterfacePlan
        {
            Name = name,
            Namespace = GeneratedNamespace.Models,
            ImplementedBy = recordNames,
            Members = [],
            Description = $"Represents the shared {GeneratedDisplayName.Of(property.Name)} shape of every {GeneratedDisplayName.Of(ownerConcept)} variant.",
        };
        if (!_interfaces.TryAdd(name, plan))
        {
            return null;
        }
        _byRecordSet[key] = name;
        // Claim before recursion, so a nested carrier cannot build the same interface a second time.
        _interfaces[name] = plan with { Members = ComposeMembers(name, recordNames) };
        foreach (var recordName in recordNames)
        {
            _implementations.AddCarrier(recordName, name);
        }

        return name;
    }

    /// <summary>Refuses a naming row no hoist answers: a stale row is a decision no longer taken.</summary>
    public void ReportUnusedRows()
    {
        foreach (var row in _namingRows.Where(row => !_usedRows.Contains(RowKey(row.Owner, row.Property))))
        {
            _errors.Add(BindingErrorCategory.Curation, row.Owner,
                $"hoisted member name curation for '{row.Property}' is unused: the interface hoists no promoted object under that property");
        }
    }

    private ReadOnlyCollection<HoistedMemberPlan> ComposeMembers(string interfaceName, IReadOnlyList<string> recordNames)
    {
        var first = _models[recordNames[0]];
        var members = new List<HoistedMemberPlan>(first.Properties.Count);
        foreach (var property in first.Properties)
        {
            var type = property.Type;
            if (PromotedRecordNames(recordNames, property) is { } promoted)
            {
                var nested = Resolve(interfaceName, interfaceName[1..], property, promoted);
                if (nested is null)
                {
                    continue;
                }

                type = new NamedTypeReferencePlan
                {
                    Name = nested,
                    IsNullable = property.Type.IsNullable,
                    JsonNullRepresentation = property.Type.JsonNullRepresentation,
                };
            }

            // The same satisfaction rule the union interface uses, applied to every member rather
            // than only to the promoted ones: a promoted record answers its carrier, and a
            // tri-state property answers the unwrapped member the interface declares, each through
            // an explicit implementation. Identity already proved every record declares this member
            // in the same shape, so the first record's plan answers for all of them.
            if (_satisfaction.RequiresExplicitImplementation(property, type))
            {
                foreach (var recordName in recordNames)
                {
                    _implementations.AddExplicitImplementation(recordName, new HoistedImplementationPlan
                    {
                        InterfaceName = interfaceName,
                        MemberName = property.Name,
                        MemberType = type,
                        PropertyName = property.Name,
                        ReadsOptionalValue = property.EmitsOptionalWrapper,
                    });
                }
            }

            members.Add(new HoistedMemberPlan
            {
                WireName = property.WireName,
                Name = property.Name,
                Type = type,
                Description = property.Description,
            });
        }

        return Array.AsReadOnly([.. members]);
    }

    /// <summary>The distinct records a property promotes across the set, or null when it names one type.</summary>
    private IReadOnlyList<string>? PromotedRecordNames(IReadOnlyList<string> recordNames, ModelPropertyPlan property) =>
        PromotedRecordSet.Resolve(
            recordNames.Select(recordName => _models[recordName]
                .Properties.First(candidate => string.Equals(candidate.WireName, property.WireName, StringComparison.Ordinal))),
            _models);

    private string? CuratedName(string owner, string wireName)
    {
        var row = _namingRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Owner, owner, StringComparison.Ordinal)
            && string.Equals(candidate.Property, wireName, StringComparison.Ordinal));
        if (row is null)
        {
            return null;
        }

        _ = _usedRows.Add(RowKey(owner, wireName));
        return row.DotNetName;
    }

    private void ReportCuratedCollision(string owner, ModelPropertyPlan property, string? curated, string existing)
    {
        if (curated is not null && !string.Equals(curated, existing, StringComparison.Ordinal))
        {
            _errors.Add(BindingErrorCategory.Naming, owner,
                $"hoisted member '{property.WireName}' shares its records with '{existing}' and cannot also be named '{curated}'");
        }
    }

    private static string RowKey(string owner, string property) => string.Concat(owner, "\0", property);
}
