using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class EndpointPolicyTests
{
    [Test]
    [Arguments("http://localhost:4096", "http://localhost:4096")]
    [Arguments("http://localhost:4096/", "http://localhost:4096")]
    [Arguments("https://host.example/prefix/", "https://host.example/prefix")]
    [Arguments("http://host.example/a/b/", "http://host.example/a/b")]
    public async Task Normalize_Should_Trim_The_Trailing_Slash_And_Preserve_The_Prefix(string endpoint, string expected)
    {
        var normalized = EndpointPolicy.Normalize(new Uri(endpoint));

        await Assert.That(normalized).IsEqualTo(expected);
    }

    [Test]
    public async Task Normalize_Should_Refuse_A_Relative_Endpoint()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = EndpointPolicy.Normalize(new Uri("/api", UriKind.Relative)));

        await Assert.That(exception.Message).Contains("absolute");
    }

    [Test]
    public async Task Normalize_Should_Refuse_A_Non_Http_Scheme()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = EndpointPolicy.Normalize(new Uri("ftp://host")));

        await Assert.That(exception.Message).Contains("scheme");
    }

    [Test]
    public async Task Normalize_Should_Refuse_A_Query()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = EndpointPolicy.Normalize(new Uri("http://host?x=1")));

        await Assert.That(exception.Message).Contains("query");
    }

    [Test]
    public async Task Normalize_Should_Refuse_A_Fragment()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = EndpointPolicy.Normalize(new Uri("http://host#frag")));

        await Assert.That(exception.Message).Contains("fragment");
    }

    [Test]
    public async Task Normalize_Should_Refuse_User_Information()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = EndpointPolicy.Normalize(new Uri("http://user:pw@host")));

        await Assert.That(exception.Message).Contains("user information");
    }
}
