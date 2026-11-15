using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpConnectUriBuilderTests
{
    [Test]
    [Arguments("http", "ws")]
    [Arguments("ws", "ws")]
    [Arguments("https", "wss")]
    [Arguments("wss", "wss")]
    public async Task Build_Should_Match_Upstream_Base_Query_Path_And_Schemes(string scheme, string expectedScheme)
    {
        var connection = new ConnectionSnapshot("https://root.test", null, new LocationSelector { Directory = "ambient" }, new ManualTimeProvider());
        var options = new ClientMcpOptions
        {
            Name = "app&é#\"",
            Tools = [],
            Location = new LocationSelector { Directory = "dir/&é" },
            Url = new Uri($"{scheme}://target.test/base?ticket=nonce&custom=one&n%61me=old&custom=two&name=duplicate&location%5Bdirectory%5D=old&empty=#gone"),
        };
        var uri = ClientMcpConnectUriBuilder.Build(connection, options);
        await Assert.That(uri.Scheme).IsEqualTo(expectedScheme);
        await Assert.That(uri.Host).IsEqualTo("target.test");
        await Assert.That(uri.AbsolutePath).IsEqualTo("/base/api/experimental/client-mcp");
        await Assert.That(uri.Fragment).IsEqualTo(string.Empty);
        await Assert.That(uri.Query.TrimStart('?').Split('&').Select(static pair => Uri.UnescapeDataString(pair.Replace('+', ' '))))
            .IsEquivalentTo(["ticket=nonce", "custom=one", "name=app&é#\"", "custom=two", "location[directory]=dir/&é", "empty="], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, null, "ambient")]
    [Arguments(false, "explicit", "explicit")]
    [Arguments(true, null, "url-dir")]
    [Arguments(true, "explicit", "explicit")]
    public async Task Build_Should_Merge_Ambient_Directory_Only_Without_An_Explicit_Url(bool explicitUrl, string? directory, string expected)
    {
        var connection = new ConnectionSnapshot("http://root.test/prefix", null, new LocationSelector { Directory = "ambient" }, new ManualTimeProvider());
        var uri = ClientMcpConnectUriBuilder.Build(connection, new ClientMcpOptions
        {
            Name = "app",
            Tools = [],
            Url = explicitUrl ? new Uri("http://url.test/?location[directory]=url-dir") : null,
            Location = directory is null ? null : new LocationSelector { Directory = directory },
        });
        await Assert.That(Uri.UnescapeDataString(uri.Query)).IsEqualTo($"?location[directory]={expected}&name=app");
    }
}
