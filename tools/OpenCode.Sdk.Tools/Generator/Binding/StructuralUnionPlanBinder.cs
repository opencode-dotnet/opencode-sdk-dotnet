using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class StructuralUnionPlanBinder
{
    public static StructuralUnionModelPlan? Bind(SchemaBindingContext context, string key, string name, UnionNode union)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(union);
        var graph = context.Document.Schemas;
        var errors = context.Errors;
        var scope = new ArmScope(key, context.Types, errors);
        var arms = scope.Arms;
        var objectArms = new List<ObjectArm>();
        var inhabitableBranchCount = 0;
        foreach (var (branch, index) in union.Branches.Select(static (branch, index) => (branch, index)))
        {
            var resolved = RefChain.Resolve(branch, graph).Node;
            if (resolved is NeverNode)
            {
                continue;
            }

            inhabitableBranchCount++;
            var arm = BindArm(scope, branch, resolved, index, sharesObjectToken: objectArms.Count > 0);
            if (arm is null)
            {
                continue;
            }

            if (resolved is ObjectNode objectNode && arm.Tokens.Contains(JsonTokenType.StartObject))
            {
                objectArms.Add(new ObjectArm(arms.Count, index, objectNode));
            }

            arms.Add(arm);
            scope.ClaimedTokens.UnionWith(arm.Tokens);
        }

        if (arms.Count < 2 || arms.Count != inhabitableBranchCount)
        {
            if (arms.Count < 2)
            {
                errors.Add(BindingErrorCategory.Schema, key,
                    "structural union must retain at least two inhabitable, distinguishable branches");
            }

            return null;
        }

        if (objectArms.Count > 1 && !TryClaimObjectArms(arms, objectArms, graph, key, errors))
        {
            return null;
        }

        return CreatePlan(name, union, arms);
    }

    /// <summary>
    /// Binds one inhabitable branch to its arm, or reports why it cannot be one. An object may
    /// share the object token with the named objects before it (<paramref name="sharesObjectToken"/>):
    /// the first one whose claim holds takes the value. Anything else that starts with an object -
    /// a dictionary, a free-form object, a marked union - takes every object, so a neighbour
    /// there is ambiguous and stays refused.
    /// </summary>
    private static StructuralUnionArmPlan? BindArm(ArmScope scope, SchemaNode branch, SchemaNode resolved, int index,
        bool sharesObjectToken)
    {
        var armNameSubject = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!TryGetTokens(resolved, out var tokens))
        {
            scope.Errors.Add(BindingErrorCategory.Schema, scope.Key,
                $"structural union branch {armNameSubject} has no deterministic JSON-token dispatch");
            return null;
        }

        var firstMatch = resolved is ObjectNode && sharesObjectToken;
        var effective = firstMatch
            ? branch
            : ResolveEffectiveBranch(scope, branch, resolved, tokens, index);
        if (effective is null)
        {
            return null;
        }

        JsonTokenType[] effectiveTokens = firstMatch ? [.. tokens] : [.. tokens.Where(token => !scope.ClaimedTokens.Contains(token))];
        if (!ValidateSpecialNumberArm(resolved, effectiveTokens, scope.Key, scope.Errors))
        {
            return null;
        }

        var type = scope.TypeBinder.BindStructuralArm(scope.Key, armNameSubject, effective);
        if (type is null || !ValidateNotBinaryArm(type, armNameSubject, scope.Key, scope.Errors))
        {
            return null;
        }

        var armName = ArmName(type);
        return ValidateArmName(armName, scope.Arms, scope.Key, scope.Errors)
            ? new StructuralUnionArmPlan { Name = armName, Type = type, Tokens = effectiveTokens }
            : null;
    }

    /// <summary>
    /// Gives every object arm that shares the object token its first-match claim, and refuses an
    /// arm an earlier arm always claims first: Effect would never reach it either, so the carrier
    /// would carry a member no value can select.
    /// </summary>
    private static bool TryClaimObjectArms(List<StructuralUnionArmPlan> arms, IReadOnlyList<ObjectArm> objectArms,
        IReadOnlyDictionary<string, SchemaNode> graph, string key, BindingErrorCollector errors)
    {
        var claims = new List<StructuralObjectClaimPlan>(objectArms.Count);
        var valid = true;
        foreach (var objectArm in objectArms)
        {
            var claim = CreateClaim(objectArm, graph, key, errors);
            if (claim is null)
            {
                valid = false;
                continue;
            }

            claims.Add(claim);
        }

        if (!valid)
        {
            return false;
        }

        for (var later = 1; later < claims.Count; later++)
        {
            for (var earlier = 0; earlier < later; earlier++)
            {
                if (!Subsumes(claims[earlier], claims[later]))
                {
                    continue;
                }

                errors.Add(BindingErrorCategory.Schema, key, string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"structural union branch {objectArms[later].BranchIndex} is unreachable: branch {objectArms[earlier].BranchIndex} claims every object it claims"));
                valid = false;
                break;
            }
        }

        if (!valid)
        {
            return false;
        }

        for (var position = 0; position < objectArms.Count; position++)
        {
            var armIndex = objectArms[position].ArmIndex;
            arms[armIndex] = arms[armIndex] with { Claim = claims[position] };
        }

        return true;
    }

    private static StructuralObjectClaimPlan? CreateClaim(ObjectArm objectArm, IReadOnlyDictionary<string, SchemaNode> graph,
        string key, BindingErrorCollector errors)
    {
        var sentinels = new List<StructuralSentinelPlan>();
        var valid = true;
        foreach (var property in objectArm.Node.Properties)
        {
            switch (RefChain.Resolve(property.Schema, graph).Node)
            {
                case LiteralNode { Kind: LiteralKind.Number }:
                    errors.Add(BindingErrorCategory.Schema, key, string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"structural union branch {objectArm.BranchIndex} constrains '{property.Name}' to a number literal; first-match sentinels are string or boolean"));
                    valid = false;
                    break;
                case LiteralNode literal:
                    sentinels.Add(new StructuralSentinelPlan { Property = property.Name, Kind = literal.Kind, Values = [literal.Value] });
                    break;
                case EnumNode enumeration:
                    sentinels.Add(new StructuralSentinelPlan { Property = property.Name, Kind = LiteralKind.String, Values = enumeration.Values });
                    break;
            }
        }

        return valid
            ? new StructuralObjectClaimPlan
            {
                RequiredKeys = [.. objectArm.Node.Properties.Where(static property => property.IsRequired).Select(static property => property.Name)],
                Sentinels = sentinels,
            }
            : null;
    }

    /// <summary>
    /// Whether every object <paramref name="later"/> claims is claimed by <paramref name="earlier"/>
    /// too: each key the earlier arm requires is required by the later one, and each property the
    /// earlier arm constrains the later one constrains to a subset of the same values.
    /// </summary>
    private static bool Subsumes(StructuralObjectClaimPlan earlier, StructuralObjectClaimPlan later) =>
        earlier.RequiredKeys.All(requiredKey => later.RequiredKeys.Contains(requiredKey, StringComparer.Ordinal))
        && earlier.Sentinels.All(sentinel => later.Sentinels.Any(candidate =>
            StringComparer.Ordinal.Equals(candidate.Property, sentinel.Property)
            && candidate.Kind == sentinel.Kind
            && candidate.Values.All(value => sentinel.Values.Contains(value, StringComparer.Ordinal))));

    private sealed record ObjectArm(int ArmIndex, int BranchIndex, ObjectNode Node);

    private sealed record ArmScope(string Key, TypePlanBinder TypeBinder, BindingErrorCollector Errors)
    {
        public List<StructuralUnionArmPlan> Arms { get; } = [];
        public HashSet<JsonTokenType> ClaimedTokens { get; } = [];
    }

    private static StructuralUnionModelPlan CreatePlan(string name, UnionNode union,
        IReadOnlyList<StructuralUnionArmPlan> arms) =>
        new()
        {
            Name = name,
            KindTypeName = $"{name}Kind",
            Namespace = GeneratedNamespace.Models,
            Description = union.Description,
            Arms = arms,
        };

    private static bool ValidateSpecialNumberArm(SchemaNode resolved, IReadOnlyList<JsonTokenType> tokens,
        string key, BindingErrorCollector errors)
    {
        if (resolved is not SpecialNumberNode || !tokens.Contains(JsonTokenType.String))
        {
            return true;
        }

        errors.Add(BindingErrorCategory.Schema, key,
            "a structural special-number arm requires an earlier text branch to own its named string spellings");
        return false;
    }

    private static bool ValidateNotBinaryArm(TypeReferencePlan type, string armNameSubject, string key,
        BindingErrorCollector errors)
    {
        if (type is not BinaryTypeReferencePlan)
        {
            return true;
        }

        // Named as a branch, not as an arm: a structural union's branches are anonymous, so this
        // subject is the positional index the sibling token-dispatch refusal above already uses.
        // There is no human arm name to quote - ArmName cannot produce one for a binary plan.
        errors.Add(BindingErrorCategory.Schema, key,
            $"structural union branch {armNameSubject} is a base64 string; binary arms are not supported");
        return false;
    }

    private static bool ValidateArmName(string armName, IReadOnlyList<StructuralUnionArmPlan> arms, string key,
        BindingErrorCollector errors)
    {
        if (armName is "Kind" or "Unknown")
        {
            errors.Add(BindingErrorCategory.Naming, key,
                $"structural union arm name '{armName}' collides with a reserved carrier member");
            return false;
        }

        if (!arms.Any(arm => StringComparer.Ordinal.Equals(arm.Name, armName)))
        {
            return true;
        }

        errors.Add(BindingErrorCategory.Naming, key,
            $"multiple structural union branches map to arm name '{armName}'");
        return false;
    }

    private static SchemaNode? ResolveEffectiveBranch(ArmScope scope, SchemaNode original, SchemaNode resolved,
        IReadOnlyList<JsonTokenType> tokens, int index)
    {
        var overlap = tokens.Where(scope.ClaimedTokens.Contains).ToArray();
        if (overlap.Length is 0)
        {
            return original;
        }

        var remaining = tokens.Where(token => !scope.ClaimedTokens.Contains(token)).ToArray();
        if (resolved is SpecialNumberNode
            && overlap is [JsonTokenType.String]
            && remaining is [JsonTokenType.Number])
        {
            // The earlier broad string branch owns named non-finite spellings. The numeric
            // remainder is an ordinary JSON number and must not write a named string itself.
            return new PrimitiveNode
            {
                Kind = PrimitiveKind.Number
            };
        }

        var tokenNames = string.Join(", ", overlap.Order().Select(static token => token.ToString()));
        scope.Errors.Add(BindingErrorCategory.Schema, scope.Key,
            $"structural union branch {index.ToString(System.Globalization.CultureInfo.InvariantCulture)} overlaps earlier branch token kind(s): {tokenNames}");
        return null;
    }

    private static bool TryGetTokens(SchemaNode branch, out IReadOnlyList<JsonTokenType> tokens)
    {
        tokens = branch switch
        {
            PrimitiveNode { Kind: PrimitiveKind.String } or EnumNode or LiteralNode { Kind: LiteralKind.String }
                or JsonStringNode or EncodedStringNode => [JsonTokenType.String],
            PrimitiveNode { Kind: PrimitiveKind.Number or PrimitiveKind.Integer }
                or LiteralNode { Kind: LiteralKind.Number } => [JsonTokenType.Number],
            PrimitiveNode { Kind: PrimitiveKind.Boolean } or LiteralNode { Kind: LiteralKind.Boolean } =>
                [JsonTokenType.True, JsonTokenType.False],
            SpecialNumberNode => [JsonTokenType.String, JsonTokenType.Number],
            ArrayNode or TupleNode => [JsonTokenType.StartArray],
            ObjectNode or DictionaryNode or FreeFormObjectNode
                or UnionNode { Classification: UnionClassification.Marked } => [JsonTokenType.StartObject],
            _ => [],
        };
        return tokens.Count > 0;
    }

    /// <summary>
    /// Names one arm from its bound type. The binary case is unreachable through
    /// <see cref="Bind"/> - <see cref="ValidateNotBinaryArm"/> refuses a binary plan before
    /// naming ever runs - and is kept spelled out anyway: the catch-all below would
    /// call a type the binder knows perfectly well "unknown", which is the wrong thing for the
    /// next reader to be told when the ordering that makes it unreachable changes.
    /// </summary>
    private static string ArmName(TypeReferencePlan type) => type switch
    {
        NamedTypeReferencePlan { Name: "string" } => "Text",
        NamedTypeReferencePlan { Name: "double" } or SpecialNumberTypeReferencePlan => "Number",
        NamedTypeReferencePlan { Name: "long" } => "WholeNumber",
        NamedTypeReferencePlan { Name: "bool" } => "Boolean",
        NamedTypeReferencePlan named => CSharpNamePolicy.ToUnionConceptName(named.Name),
        ListTypeReferencePlan list => $"{ArmName(list.ElementType)}List",
        DictionaryTypeReferencePlan dictionary => $"{ArmName(dictionary.ValueType)}Dictionary",
        BinaryTypeReferencePlan => throw new InvalidOperationException("Binary arms are refused before naming."),
        _ => throw new InvalidOperationException($"Unknown structural union arm type '{type.GetType().Name}'."),
    };
}
