using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Decides which model members a generated <c>ToString()</c> masks (ADR-0028). The OpenAPI
/// document carries no secret marker, so the floor is upstream's own list: the fields its HTTP
/// recorder redacts from every recorded JSON body (<c>DEFAULT_REDACT_JSON_FIELDS</c> in
/// <c>packages/http-recorder/src/redaction/redactor.ts</c>, source-watched), matched the way it
/// matches them. Curation rows add or lift a mask with a reason. Beside the floor stands a wall:
/// a member whose wire name carries one of the words upstream reads as a secret marker in an
/// environment-variable name (<c>ENV_SECRET_NAMES</c> in <c>secrets.ts</c>) refuses the bind
/// until a row decides it, so a new credential-shaped member never prints unexamined. It runs
/// once every member a model carries is known, so it judges the members as they are emitted.
/// An open model's extension data has no wire name for the floor or the wall to match, so a row
/// keyed by the model decides it, and an open model no row decides refuses the bind the same way.
/// A union whose known arms reach a masked member also masks the payload its unknown arm
/// preserves, because an arm the pin does not know may carry the same secret.
/// </summary>
internal static class SecretMemberPolicy
{
    /// <summary>The secret-name wall's problem text: a missing curation row, never a shape wall, so the pending-operation probe sets it aside.</summary>
    public const string UndecidedSecretProblem =
        "member name looks like a secret and no curation row decides it: add a redactedMembers row, or a secretLookingNames row when the value is never a credential";

    /// <summary>The open-bag wall's problem text: a missing curation row, never a shape wall, so the pending-operation probe sets it aside.</summary>
    public const string UndecidedOpenMembersProblem =
        "model leaves members open and no curation row decides them: add a redactedOpenMembers row, redact true when the open members can carry a credential, redact false when they never do";

    /// <summary>The recorder's field list, normalized; see <see cref="Normalize"/>.</summary>
    private static readonly HashSet<string> UpstreamRedactedFields = new(
        ["accesstoken", "apikey", "clientsecret", "password", "refreshtoken", "secret", "token"],
        StringComparer.Ordinal);

    /// <summary>The words the recorder's environment scan reads as a secret marker, matched case-insensitively anywhere in a name.</summary>
    private static readonly string[] SecretMarkerWords = ["API", "AUTH", "BEARER", "CREDENTIAL", "KEY", "PASSWORD", "SECRET", "TOKEN"];

    public static IReadOnlyList<ModelPlan> Apply(IReadOnlyList<ModelPlan> models, GenerationCuration curation, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        var objects = models.OfType<ObjectModelPlan>().ToArray();
        var rows = ValidateMemberRows(objects, curation.RedactedMembers, errors);
        var cleared = ValidateNameRows(objects, curation.SecretLookingNames, errors);
        var openBags = ValidateOpenMemberRows(objects, curation.RedactedOpenMembers, errors);

        return
        [
            .. models.Select(model => model is ObjectModelPlan objectModel
                ? objectModel with
                {
                    Properties = [.. objectModel.Properties.Select(property => Decide(objectModel, property, rows, cleared, errors))],
                    RedactsExtensionData = openBags.TryGetValue(objectModel.Name, out var redact) && redact,
                }
                : model),
        ];
    }

    /// <summary>
    /// Flags every union whose known arms reach a masked member at any depth: through an object's
    /// members, a list's elements, a dictionary's values, a nested union's arms, or a structural
    /// union's arms. The reach is a least fixpoint over the named-type graph, so a schema that
    /// refers to itself settles instead of recursing. Run it after <see cref="Apply"/> has decided
    /// every member's mask.
    /// </summary>
    /// <param name="models">The bound models, member masks decided.</param>
    /// <param name="unions">The bound marked unions.</param>
    /// <returns>The same plans, each union that reaches a mask flagged to mask its unknown payload.</returns>
    public static UnknownPayloadMaskResult MaskUnknownPayloads(IReadOnlyList<ModelPlan> models, IReadOnlyList<UnionPlan> unions)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(unions);

        // Each named type points at the types its printed form prints: an object at its members'
        // types, a structural union at its arms' types, a marked union at its known arms.
        var references = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var reaching = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (model is ObjectModelPlan objectModel)
            {
                references[objectModel.Name] = [.. objectModel.Properties.SelectMany(static property => NamedTypes(property.Type))];
                if (objectModel.RedactsExtensionData || objectModel.Properties.Any(static property => property.IsRedacted))
                {
                    _ = reaching.Add(objectModel.Name);
                }
            }
            else if (model is StructuralUnionModelPlan structural)
            {
                references[structural.Name] = [.. structural.Arms.SelectMany(static arm => NamedTypes(arm.Type))];
            }
        }

        foreach (var union in unions)
        {
            references[union.Name] =
            [
                .. union.Variants.Select(static variant => variant.TypeName),
                .. union.PrefixVariant is { } prefix ? [prefix.TypeName] : Array.Empty<string>(),
            ];
        }

        bool grew;
        do
        {
            grew = false;
            foreach (var (name, targets) in references)
            {
                if (!reaching.Contains(name) && targets.Any(reaching.Contains))
                {
                    grew = reaching.Add(name) || grew;
                }
            }
        }
        while (grew);

        return new UnknownPayloadMaskResult
        {
            Models =
            [
                .. models.Select(model => model is StructuralUnionModelPlan structural && reaching.Contains(structural.Name)
                    ? structural with { MasksUnknownPayload = true }
                    : model),
            ],
            Unions = [.. unions.Select(union => reaching.Contains(union.Name) ? union with { MasksUnknownPayload = true } : union)],
        };
    }

    /// <summary>The recorder's <c>normalizeField</c>: every character outside <c>[a-z0-9]</c> (either case) dropped, then lower-cased.</summary>
    /// <param name="wireName">The member's wire name.</param>
    /// <returns>The normalized name.</returns>
    public static string Normalize(string wireName) =>
        new([.. wireName.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>Whether upstream's recorder masks a field of this name.</summary>
    /// <param name="wireName">The member's wire name.</param>
    /// <returns>True when the normalized name is on the list.</returns>
    public static bool IsUpstreamRedacted(string wireName) => UpstreamRedactedFields.Contains(Normalize(wireName));

    /// <summary>Whether the name carries a word upstream reads as a secret marker.</summary>
    /// <param name="wireName">The member's wire name.</param>
    /// <returns>True when a marker word occurs anywhere in the name, in any case.</returns>
    public static bool LooksSecret(string wireName) =>
        SecretMarkerWords.Any(word => wireName.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> NamedTypes(TypeReferencePlan type) => type switch
    {
        NamedTypeReferencePlan named => [named.Name],
        ListTypeReferencePlan list => NamedTypes(list.ElementType),
        DictionaryTypeReferencePlan dictionary => NamedTypes(dictionary.ValueType),
        _ => [],
    };

    private static ModelPropertyPlan Decide(ObjectModelPlan model, ModelPropertyPlan property,
        Dictionary<(string Model, string Property), RedactedMemberCuration> rows, HashSet<string> cleared, BindingErrorCollector errors)
    {
        var floor = IsUpstreamRedacted(property.WireName);
        if (rows.TryGetValue((model.Name, property.WireName), out var row))
        {
            return property with { IsRedacted = row.Redact };
        }

        if (!floor && LooksSecret(property.WireName) && !cleared.Contains(property.WireName))
        {
            errors.Add(BindingErrorCategory.Curation, $"{model.Name}.{property.WireName}", UndecidedSecretProblem);
        }

        return floor ? property with { IsRedacted = true } : property;
    }

    private static Dictionary<(string Model, string Property), RedactedMemberCuration> ValidateMemberRows(
        ObjectModelPlan[] models, IReadOnlyList<RedactedMemberCuration> rows, BindingErrorCollector errors)
    {
        var byName = models.ToDictionary(static model => model.Name, StringComparer.Ordinal);
        var result = new Dictionary<(string Model, string Property), RedactedMemberCuration>();
        foreach (var row in rows)
        {
            var subject = $"{row.Model}.{row.Property}";
            if (!result.TryAdd((row.Model, row.Property), row))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "redacted member curation is duplicated");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "redacted member curation must declare a reason");
            }

            if (!byName.TryGetValue(row.Model, out var model)
                || !model.Properties.Any(property => string.Equals(property.WireName, row.Property, StringComparison.Ordinal)))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "redacted member curation names no generated model member");
                continue;
            }

            var floor = IsUpstreamRedacted(row.Property);
            if (row.Redact && floor)
            {
                errors.Add(BindingErrorCategory.Curation, subject, "redacted member curation repeats the upstream redaction list");
            }
            else if (!row.Redact && !floor)
            {
                errors.Add(BindingErrorCategory.Curation, subject, "redacted member curation lifts a mask the upstream redaction list never applies");
            }
        }

        return result;
    }

    private static Dictionary<string, bool> ValidateOpenMemberRows(ObjectModelPlan[] models, IReadOnlyList<RedactedOpenMembersCuration> rows,
        BindingErrorCollector errors)
    {
        var byName = models.ToDictionary(static model => model.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!result.TryAdd(row.Model, row.Redact))
            {
                errors.Add(BindingErrorCategory.Curation, row.Model, "redacted open members curation is duplicated");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, row.Model, "redacted open members curation must declare a reason");
            }

            if (!byName.TryGetValue(row.Model, out var model))
            {
                errors.Add(BindingErrorCategory.Curation, row.Model, "redacted open members curation names no generated model");
            }
            else if (!model.EmitsExtensionData)
            {
                errors.Add(BindingErrorCategory.Curation, row.Model, "redacted open members curation names a model that leaves no member open");
            }
        }

        foreach (var model in models.Where(model => model.EmitsExtensionData && !result.ContainsKey(model.Name)))
        {
            errors.Add(BindingErrorCategory.Curation, model.Name, UndecidedOpenMembersProblem);
        }

        return result;
    }

    private static HashSet<string> ValidateNameRows(ObjectModelPlan[] models, IReadOnlyList<SecretLookingNameCuration> rows,
        BindingErrorCollector errors)
    {
        var wireNames = models
            .SelectMany(static model => model.Properties)
            .Select(static property => property.WireName)
            .ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!result.Add(row.Property))
            {
                errors.Add(BindingErrorCategory.Curation, row.Property, "secret-looking name curation is duplicated");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, row.Property, "secret-looking name curation must declare a reason");
            }

            if (!wireNames.Contains(row.Property))
            {
                errors.Add(BindingErrorCategory.Curation, row.Property, "secret-looking name curation names no generated model member");
            }
            else if (IsUpstreamRedacted(row.Property))
            {
                errors.Add(BindingErrorCategory.Curation, row.Property, "secret-looking name curation clears a name the upstream redaction list masks");
            }
            else if (!LooksSecret(row.Property))
            {
                errors.Add(BindingErrorCategory.Curation, row.Property, "secret-looking name curation names a member the secret-name wall never stops");
            }
        }

        return result;
    }
}
