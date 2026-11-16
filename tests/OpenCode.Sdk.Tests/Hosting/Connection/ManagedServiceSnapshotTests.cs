using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class ManagedServiceSnapshotTests
{
    [Test]
    public async Task ReconnectRequest_Should_Keep_The_Captured_Inputs_Without_Policy_Or_OnStart()
    {
        var request = EnsureRequest.Snapshot(new OpenCodeServerEnsureOptions
        {
            ExpectedVersion = "2.2.7",
            VersionPolicy = OpenCodeServerVersionPolicy.Replace,
            OnStart = _ => throw new InvalidOperationException("must not forward"),
        });
        var paths = new ServicePaths("registration.json", null, [], null);
        var overlay = new ServiceConfigEnvironment(Entries: null);
        var snapshot = new ManagedServiceSnapshot(request, paths, overlay);

        var reconnect = snapshot.ReconnectRequest;

        await Assert.That(snapshot.Paths).IsSameReferenceAs(paths);
        await Assert.That(snapshot.Overlay).IsSameReferenceAs(overlay);
        await Assert.That(reconnect.Selection).IsSameReferenceAs(request.Selection);
        await Assert.That(reconnect.Command).IsSameReferenceAs(request.Command);
        await Assert.That(reconnect.Environment).IsSameReferenceAs(request.Environment);
        await Assert.That(reconnect.Policy).IsEqualTo(OpenCodeServerVersionPolicy.Ignore);
        await Assert.That(reconnect.LoopVersion).IsNull();
        await Assert.That(reconnect.OnStart).IsNull();
    }
}
