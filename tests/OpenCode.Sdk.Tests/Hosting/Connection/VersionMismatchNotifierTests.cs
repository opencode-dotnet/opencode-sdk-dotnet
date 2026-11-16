using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class VersionMismatchNotifierTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4096");

    [Test]
    public async Task Notify_Should_Fire_Once_With_The_Exact_Context()
    {
        var contexts = new List<OpenCodeServerVersionMismatchContext>();

        VersionMismatchNotifier.Notify(Endpoint, "2.2.9", contexts.Add);

        await Assert.That(contexts.Count).IsEqualTo(1);
        await Assert.That(contexts[0].Endpoint).IsEqualTo(Endpoint);
        await Assert.That(contexts[0].ServerVersion).IsEqualTo("2.2.9");
        await Assert.That(contexts[0].ClientVersion).IsEqualTo("2.3.0");
    }

    [Test]
    public async Task Notify_Should_Stay_Silent_Without_A_Callback_Or_On_A_Match()
    {
        var contexts = new List<OpenCodeServerVersionMismatchContext>();

        VersionMismatchNotifier.Notify(Endpoint, "2.2.9", callback: null);
        VersionMismatchNotifier.Notify(Endpoint, "2.3.0", contexts.Add);

        await Assert.That(contexts).IsEmpty();
    }

    [Test]
    public async Task Notify_Should_Propagate_A_Callback_Exception()
    {
        var cause = new InvalidOperationException("consumer callback failed");

        var failure = await Assert.That(() => VersionMismatchNotifier.Notify(Endpoint, "2.2.9", _ => throw cause))
            .Throws<InvalidOperationException>();

        await Assert.That(failure).IsSameReferenceAs(cause);
    }
}
