namespace OpenCode.Sdk.Internal;

/// <summary>
/// Suppresses one slopwatch rule at one site, with the reason the scanner requires — the product
/// twin of the test projects' attribute, matched by name. Product code uses it where a pinned
/// upstream behavior is itself a deliberate swallow (<c>Effect.ignore</c>, <c>.catch(() =&gt;
/// undefined)</c>), so the catch says so instead of returning a result nobody reads.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
internal sealed class SlopwatchSuppressAttribute : Attribute
{
    public SlopwatchSuppressAttribute(string ruleId, string reason)
    {
        RuleId = ruleId;
        Reason = reason;
    }

    public string RuleId { get; }

    public string Reason { get; }
}
