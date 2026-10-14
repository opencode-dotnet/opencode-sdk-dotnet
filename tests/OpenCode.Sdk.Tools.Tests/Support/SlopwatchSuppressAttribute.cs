namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>Suppresses one slopwatch rule at one site, with the reason the scanner requires.</summary>
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
