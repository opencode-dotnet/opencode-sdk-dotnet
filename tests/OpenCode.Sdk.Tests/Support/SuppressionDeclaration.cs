using System.Reflection;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

internal static class SuppressionDeclaration
{
    public static SlopwatchSuppressAttribute[] BestEffortFailures(MemberInfo member) =>
        [.. member.GetCustomAttributes<SlopwatchSuppressAttribute>()
            .Where(attribute => string.Equals(attribute.RuleId, "SW003", StringComparison.Ordinal))];
}
