using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtyConnectUriBuilderTests
{
    private const string PtyId = "pty_100";

    [Test]
    public async Task Build_Should_Upgrade_An_Http_Endpoint_To_The_Ws_Scheme()
    {
        var uri = PtyConnectUriBuilder.Build(Snapshot("http://localhost:4096"), PtyId, options: null);

        await Assert.That(uri.ToString()).IsEqualTo("ws://localhost:4096/api/pty/pty_100/connect");
    }

    [Test]
    public async Task Build_Should_Upgrade_An_Https_Endpoint_To_The_Wss_Scheme()
    {
        var uri = PtyConnectUriBuilder.Build(Snapshot("https://opencode.example:8443/base"), PtyId, options: null);

        await Assert.That(uri.ToString()).IsEqualTo("wss://opencode.example:8443/base/api/pty/pty_100/connect");
    }

    [Test]
    public async Task Build_Should_Carry_The_Ambient_Location_When_The_Call_Sets_None()
    {
        var connection = Snapshot("http://localhost:4096", new LocationSelector { Directory = "/repo" });

        var uri = PtyConnectUriBuilder.Build(connection, PtyId, options: null);

        await Assert.That(uri.Query).IsEqualTo("?location[directory]=%2Frepo");
    }

    [Test]
    public async Task Build_Should_Merge_The_Per_Call_Location_Over_The_Ambient_One_Member_By_Member()
    {
        var connection = Snapshot("http://localhost:4096", new LocationSelector { Directory = "/amb" });
        var options = new PtyConnectOptions { Location = new LocationSelector { Directory = "/per" } };

        var uri = PtyConnectUriBuilder.Build(connection, PtyId, options);

        await Assert.That(uri.Query).IsEqualTo("?location[directory]=%2Fper");
    }

    [Test]
    public async Task Build_Should_Carry_The_Per_Call_Location_When_There_Is_No_Ambient_One()
    {
        var options = new PtyConnectOptions { Location = new LocationSelector { Directory = "/per" } };

        var uri = PtyConnectUriBuilder.Build(Snapshot("http://localhost:4096"), PtyId, options);

        await Assert.That(uri.Query).IsEqualTo("?location[directory]=%2Fper");
    }

    [Test]
    public async Task Build_Should_Omit_The_Cursor_For_A_Full_Replay()
    {
        var uri = PtyConnectUriBuilder.Build(Snapshot("http://localhost:4096"), PtyId, new PtyConnectOptions());

        await Assert.That(uri.Query).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Build_Should_Carry_The_Live_Only_Cursor()
    {
        var uri = PtyConnectUriBuilder.Build(Snapshot("http://localhost:4096"), PtyId, new PtyConnectOptions { Cursor = -1 });

        await Assert.That(uri.Query).IsEqualTo("?cursor=-1");
    }

    [Test]
    public async Task Build_Should_Carry_A_Resume_Cursor_Beside_The_Location()
    {
        var connection = Snapshot("http://localhost:4096", new LocationSelector { Directory = "/repo" });
        var options = new PtyConnectOptions { Cursor = 4096, SendTimeout = TimeSpan.FromSeconds(2) };

        var uri = PtyConnectUriBuilder.Build(connection, PtyId, options);

        await Assert.That(uri.Query).IsEqualTo("?location[directory]=%2Frepo&cursor=4096");
    }

    [Test]
    public async Task Build_Should_Escape_The_Route_Value()
    {
        var uri = PtyConnectUriBuilder.Build(Snapshot("http://localhost:4096"), "pty 1/2", options: null);

        await Assert.That(uri.OriginalString).IsEqualTo("ws://localhost:4096/api/pty/pty%201%2F2/connect");
    }

    /// <summary>
    /// The guard is <see cref="RouteValuePolicy.EscapeSegment"/>'s, so both connect builders
    /// carry it identically; before it moved there this family had none.
    /// </summary>
    [Test]
    public async Task Build_Should_Refuse_A_Dot_Segment_Route_Value()
    {
        var connection = Snapshot("http://localhost:4096");

        _ = Assert.Throws<ArgumentException>(() => _ = PtyConnectUriBuilder.Build(connection, ".", options: null));
        _ = Assert.Throws<ArgumentException>(() => _ = PtyConnectUriBuilder.Build(connection, "..", options: null));

        await Task.CompletedTask;
    }

    private static ConnectionSnapshot Snapshot(string endpoint, LocationSelector? location = null) =>
        new(EndpointPolicy.Normalize(new Uri(endpoint)), authorization: null, location);
}
