using System.Reflection;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Terminal;

public sealed class TerminalSendQueueTests
{
    [Test]
    public async Task CloseAsync_Should_Declare_A_Reasoned_Best_Effort_Close_Suppression()
    {
        var close = typeof(TerminalSendQueue).GetMethod(nameof(TerminalSendQueue.CloseAsync));
        var suppressions = close!.GetCustomAttributes<SlopwatchSuppressAttribute>()
            .Where(attribute => string.Equals(attribute.RuleId, "SW003", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(suppressions).Count().IsEqualTo(1);
        await Assert.That(suppressions.Single().Reason).IsEqualTo(
            "Expected graceful-close failure is superseded by the owner's unconditional socket teardown " +
            "and receiver/send joins in DisposeAsync; disposal completion is the observable cleanup outcome.");
    }
}
