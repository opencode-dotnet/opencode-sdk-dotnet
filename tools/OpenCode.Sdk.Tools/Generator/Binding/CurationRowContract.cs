using System.Globalization;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>The closed registry of row identities and their common authoring refusals.</summary>
internal static class CurationRowContract
{
    private static readonly IReadOnlyDictionary<string, IRowRegistration> Registry = new Dictionary<string, IRowRegistration>(StringComparer.Ordinal)
    {
        [nameof(GenerationCuration.Descriptions)] = new RowRegistration<DescriptionCuration>("descriptions", "description",
            [("type", static row => row.Type)], static row => row.Type + (row.Member is null ? string.Empty : "." + row.Member))
        {
            DuplicateKey = static row => CompositeKey([row.Type, row.Member]),
        },
        [nameof(GenerationCuration.SessionScopedEvent)] = new RowRegistration<SessionScopedEventCuration>("sessionScopedEvent", "session-scoped event",
            [], static _ => "sessionScopedEvent"),
        [nameof(GenerationCuration.Groups)] = new RowRegistration<KeyValuePair<string, GroupCuration>>("groups", "group",
            [("group", static pair => pair.Key)], static pair => pair.Key)
        {
            Row = static pair => pair.Value,
        },
        [nameof(GenerationCuration.OperationIdentities)] = new RowRegistration<OperationIdentityCuration>("operationIdentities", "operation identity",
            [("operationId", static row => row.OperationId)], static row => row.OperationId),
        [nameof(GenerationCuration.OperationNames)] = new RowRegistration<OperationNameCuration>("operationNames", "operation name",
            [("operationId", static row => row.OperationId)], static row => row.OperationId),
        [nameof(GenerationCuration.ParameterNames)] = new RowRegistration<ParameterNameCuration>("parameterNames", "parameter name",
            [("operationId", static row => row.OperationId), ("parameter", static row => row.Parameter)], static row => row.OperationId + ":" + row.Parameter),
        [nameof(GenerationCuration.SchemaNames)] = new RowRegistration<SchemaNameCuration>("schemaNames", "schema name",
            [("schema", static row => row.Schema)], static row => row.Schema),
        [nameof(GenerationCuration.EnvelopePayloadNames)] = new RowRegistration<EnvelopePayloadNameCuration>("envelopePayloadNames", "envelope payload name",
            [("operationId", static row => row.OperationId)], static row => row.OperationId),
        [nameof(GenerationCuration.SchemaAliases)] = new RowRegistration<SchemaAlias>("schemaAliases", "schema alias",
            [("schema", static row => row.Schema)], static row => row.Schema),
        [nameof(GenerationCuration.TransportOwned)] = new RowRegistration<TransportOwnedCuration>("transportOwned", "transport-owned",
            [("operationId", static row => row.OperationId)], static row => row.OperationId),
        [nameof(GenerationCuration.HoistedMemberNames)] = new RowRegistration<HoistedMemberNameCuration>("hoistedMemberNames", "hoisted member name",
            [("owner", static row => row.Owner), ("property", static row => row.Property)], static row => row.Owner),
        [nameof(GenerationCuration.EnumMemberNames)] = new RowRegistration<EnumMemberNameCuration>("enumMemberNames", "enum member name",
            [("schema", static row => row.Schema), ("value", static row => row.Value)], static row => row.Schema + "/" + row.Value),
        [nameof(GenerationCuration.RedactedMembers)] = new RowRegistration<RedactedMemberCuration>("redactedMembers", "redacted member",
            [("model", static row => row.Model), ("property", static row => row.Property)], static row => row.Model + "." + row.Property),
        [nameof(GenerationCuration.RedactedOpenMembers)] = new RowRegistration<RedactedOpenMembersCuration>("redactedOpenMembers", "redacted open members",
            [("model", static row => row.Model)], static row => row.Model),
        [nameof(GenerationCuration.SecretLookingNames)] = new RowRegistration<SecretLookingNameCuration>("secretLookingNames", "secret-looking name",
            [("property", static row => row.Property)], static row => row.Property),
        [nameof(GenerationCuration.Declined)] = new RowRegistration<DeclinedCuration>("declined", "declined",
            [("operationId", static row => row.OperationId)], static row => row.OperationId),
        [nameof(GenerationCuration.ModelOwners)] = new RowRegistration<ModelOwnerCuration>("modelOwners", "model owner",
            [("match", static row => row.Match)], static row => row.Match)
        {
            Comparer = StringComparer.OrdinalIgnoreCase,
        },
        [nameof(GenerationCuration.SessionAreas)] = new RowRegistration<SessionAreaCuration>("sessionAreas", "session area",
            [("area", static row => row.Area)], static row => row.Area)
        {
            Comparer = StringComparer.OrdinalIgnoreCase,
        },
        [nameof(GenerationCuration.QueryParameterTypes)] = new RowRegistration<QueryParameterTypeCuration>("queryParameterTypes", "query parameter type",
            [("operationId", static row => row.OperationId), ("parameter", static row => row.Parameter)], static row => row.OperationId + ":" + row.Parameter),
        [nameof(GenerationCuration.StringQueryParameters)] = new RowRegistration<StringQueryParameterCuration>("stringQueryParameters", "string query parameter",
            [("operationId", static row => row.OperationId), ("parameter", static row => row.Parameter)], static row => row.OperationId + ":" + row.Parameter),
    };

    internal static IEnumerable<string> RegisteredProperties => Registry.Keys;

    internal static string RegisteredSection(string property) => Registry[property].Section;

    internal static bool Validate<TRow>(string property, IEnumerable<TRow> rows, BindingErrorCollector errors)
    {
        if (!Registry.TryGetValue(property, out var entry) || entry is not RowRegistration<TRow> registration)
        {
            throw new InvalidOperationException($"Unregistered curation row property/type: {property}/{typeof(TRow).Name}.");
        }

        var safe = true;
        var seen = new HashSet<string>(registration.Comparer);
        var index = 0;
        foreach (var item in rows)
        {
            var fallback = registration.Section + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            index++;
            var row = registration.Row(item);
            if (row is null)
            {
                if (item is KeyValuePair<string, GroupCuration> pair)
                {
                    fallback = registration.Section + "." + pair.Key;
                }

                errors.Add(BindingErrorCategory.Curation, fallback, fallback + " curation is null");
                safe = false;
                continue;
            }

            var usable = true;
            foreach (var field in registration.Fields.Where(field => string.IsNullOrWhiteSpace(field.Get(item))))
            {
                errors.Add(BindingErrorCategory.Curation, fallback, fallback + " curation names no " + field.Name);
                usable = false;
            }

            var subject = usable ? registration.Subject(item) : fallback;
            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, subject, registration.Label + " curation must declare a reason");
            }

            if (usable && !seen.Add(registration.DuplicateKey?.Invoke(item) ?? CompositeKey(registration.Fields.Select(field => field.Get(item)))))
            {
                errors.Add(BindingErrorCategory.Curation, subject, registration.Label + " curation is duplicated");
            }

            safe &= usable;
        }

        return safe;
    }

    internal static void ValidateBeforeAliases(GenerationCuration curation, BindingErrorCollector errors)
    {
        bool[] safe =
        [
            Validate(nameof(curation.Groups), curation.Groups.OrderBy(static pair => pair.Key, StringComparer.Ordinal), errors),
            Validate(nameof(curation.OperationNames), curation.OperationNames, errors),
            Validate(nameof(curation.ParameterNames), curation.ParameterNames, errors),
            Validate(nameof(curation.SchemaNames), curation.SchemaNames, errors),
            Validate(nameof(curation.EnvelopePayloadNames), curation.EnvelopePayloadNames, errors),
            Validate(nameof(curation.SchemaAliases), curation.SchemaAliases, errors),
            Validate(nameof(curation.TransportOwned), curation.TransportOwned, errors),
            Validate(nameof(curation.EnumMemberNames), curation.EnumMemberNames, errors),
            Validate(nameof(curation.Declined), curation.Declined, errors),
            Validate(nameof(curation.QueryParameterTypes), curation.QueryParameterTypes, errors),
            Validate(nameof(curation.StringQueryParameters), curation.StringQueryParameters, errors),
        ];
        if (safe.Contains(false))
        {
            errors.ThrowIfAny();
        }
    }

    private static string CompositeKey(IEnumerable<string?> parts) => string.Concat(parts.Select(static part =>
        part is null ? "-1:" : part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));

    private interface IRowRegistration
    {
        public string Section { get; }
    }

    private sealed record RowRegistration<TRow>(string Section, string Label,
        IReadOnlyList<(string Name, Func<TRow, string?> Get)> Fields, Func<TRow, string> Subject) : IRowRegistration
    {
        public Func<TRow, IReasonedRow?> Row { get; init; } = static row => row as IReasonedRow;

        public StringComparer Comparer { get; init; } = StringComparer.Ordinal;

        public Func<TRow, string>? DuplicateKey { get; init; }
    }
}
