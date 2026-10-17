using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.PersistentPtys;

public sealed class PersistentPtyConnectUriBuilderTests
{
    private const string PtyId = "pty_persistent_7";

    [Test]
    public async Task Build_Should_Negotiate_The_Framed_Input_Protocol_By_Default()
    {
        var uri = PersistentPtyConnectUriBuilder.Build(Snapshot("http://localhost:4096"), PtyId, options: null);

        await Assert.That(uri.ToString())
            .IsEqualTo("ws://localhost:4096/api/experimental/persistent-pty/pty_persistent_7/connect?input_protocol=1");
    }

    [Test]
    public async Task Build_Should_Carry_Wire_Options_And_Omit_The_Local_Send_Timeout()
    {
        var options = new PersistentPtyConnectOptions
        {
            Cursor = 42,
            Role = PersistentPtyRole.Observer,
            AttachmentId = "att_1",
            Takeover = true,
            SendTimeout = TimeSpan.FromSeconds(2),
        };

        var uri = PersistentPtyConnectUriBuilder.Build(Snapshot("http://localhost:4096"), PtyId, options);

        await Assert.That(uri.Query).IsEqualTo("?cursor=42&role=observer&attachment_id=att_1&takeover=true&input_protocol=1");
    }

    [Test]
    public async Task Build_Should_Upgrade_An_Https_Endpoint_To_The_Wss_Scheme()
    {
        var uri = PersistentPtyConnectUriBuilder.Build(Snapshot("https://opencode.example:8443/base"), PtyId, options: null);

        await Assert.That(uri.ToString())
            .IsEqualTo("wss://opencode.example:8443/base/api/experimental/persistent-pty/pty_persistent_7/connect?input_protocol=1");
    }

    [Test]
    public async Task Build_Should_Refuse_A_Dot_Segment_Route_Value()
    {
        var connection = Snapshot("http://localhost:4096");

        _ = Assert.Throws<ArgumentException>(() => _ = PersistentPtyConnectUriBuilder.Build(connection, ".", options: null));
        _ = Assert.Throws<ArgumentException>(() => _ = PersistentPtyConnectUriBuilder.Build(connection, "..", options: null));

        await Task.CompletedTask;
    }

    private static ConnectionSnapshot Snapshot(string endpoint) =>
        new(EndpointPolicy.Normalize(new Uri(endpoint)), authorization: null, location: null, new ManualTimeProvider());
}
