using System.Reflection;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Terminal;

public sealed class TerminalSendQueueTests
{
    [Test]
    public async Task CloseAsync_Should_Declare_A_Reasoned_Best_Effort_Close_Suppression()
    {
        var close = typeof(TerminalSendQueue).GetMethod(nameof(TerminalSendQueue.CloseAsync));
        var suppression = close!.GetCustomAttributes<SlopwatchSuppressAttribute>()
            .SingleOrDefault(attribute => attribute.RuleId is "SW003");

        await Assert.That(suppression).IsNotNull();
        await Assert.That(suppression!.Reason).IsEqualTo(
            "Expected graceful-close failure is superseded by the owner's unconditional socket teardown " +
            "and receiver/send joins in DisposeAsync; disposal completion is the observable cleanup outcome.");
    }
}
