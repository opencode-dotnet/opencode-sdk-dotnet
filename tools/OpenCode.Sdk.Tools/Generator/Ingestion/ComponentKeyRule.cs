using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion;

/// <summary>
/// The OpenAPI reader's components-key rule without its match timeout. The library rule's
/// interpreted regex checks a 100 ms wall-clock timeout on every match, so a thread paused by a
/// garbage collection or the scheduler fails a match that does only linear work, and a valid spec
/// intermittently fails to load. This rule walks the same component maps with the same pattern
/// text, so it accepts and refuses exactly the same keys, and it reports the same rule name and
/// error. Remove it when the library drops the timeout.
/// </summary>
internal static partial class ComponentKeyRule
{
    private const string RuleName = nameof(OpenApiComponentsRules.KeyMustBeRegularExpression);

    /// <summary>The replacement rule; one instance, so a rule set can be checked for it by reference.</summary>
    internal static readonly ValidationRule<OpenApiComponents> Rule = new(RuleName, static (context, components) =>
    {
        ValidateKeys(context, components.Schemas?.Keys, "schemas");
        ValidateKeys(context, components.Responses?.Keys, "responses");
        ValidateKeys(context, components.Parameters?.Keys, "parameters");
        ValidateKeys(context, components.Examples?.Keys, "examples");
        ValidateKeys(context, components.RequestBodies?.Keys, "requestBodies");
        ValidateKeys(context, components.Headers?.Keys, "headers");
        ValidateKeys(context, components.SecuritySchemes?.Keys, "securitySchemes");
        ValidateKeys(context, components.Links?.Keys, "links");
        ValidateKeys(context, components.Callbacks?.Keys, "callbacks");
    });

    /// <summary>
    /// The library's default rule set with its components-key rule replaced by this one.
    /// </summary>
    public static ValidationRuleSet CreateReaderRuleSet()
    {
        var ruleSet = ValidationRuleSet.GetDefaultRuleSet();
        var libraryRules = ruleSet
            .FindRules(typeof(OpenApiComponents))
            .Where(static rule => string.Equals(rule.Name, RuleName, StringComparison.Ordinal))
            .ToList();
        if (libraryRules.Count != 1)
        {
            throw new InvalidOperationException(
                $"The OpenAPI reader's default rule set holds {libraryRules.Count.ToString(CultureInfo.InvariantCulture)} '{RuleName}' rules; expected exactly one to replace.");
        }

        if (!ruleSet.Update(typeof(OpenApiComponents), Rule, libraryRules[0]))
        {
            throw new InvalidOperationException($"The OpenAPI reader's '{RuleName}' rule could not be replaced.");
        }

        // The replacement is appended after the rule it removes, so its position matches the
        // library's only while it is the one rule on components; a second rule would reorder the
        // reported errors silently.
        var componentRules = ruleSet.FindRules(typeof(OpenApiComponents)).Count;
        if (componentRules != 1)
        {
            throw new InvalidOperationException(
                $"The OpenAPI reader's default rule set holds {componentRules.ToString(CultureInfo.InvariantCulture)} components rules; the '{RuleName}' replacement keeps the library's error order only as the sole one.");
        }

        return ruleSet;
    }

    private static void ValidateKeys(IValidationContext context, IEnumerable<string>? keys, string component)
    {
        if (keys is null)
        {
            return;
        }

        foreach (var key in keys.Where(static key => !KeyRegex().IsMatch(key)))
        {
            context.CreateError(
                RuleName,
                $"The key '{key}' in '{component}' of components MUST match the regular expression '{KeyRegex()}'.");
        }
    }

    // The library's pattern text, unchanged, so its exact semantics carry over, including the '$'
    // that also matches before one trailing newline. The match is linear, so it needs no timeout.
    [GeneratedRegex(@"^[a-zA-Z0-9\.\-_]+$", RegexOptions.None, matchTimeoutMilliseconds: Timeout.Infinite)]
    internal static partial Regex KeyRegex();
}
