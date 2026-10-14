using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class UserAgentPolicyTests
{
    [Test]
    [Arguments("1.2.3", "OpenCode.Sdk/1.2.3")]
    [Arguments("1.0.0-alpha.3", "OpenCode.Sdk/1.0.0-alpha.3")]
    [Arguments("1.0.0+4f2a91cde800", "OpenCode.Sdk/1.0.0")]
    [Arguments("1.0.0-alpha+4f2a91cde800", "OpenCode.Sdk/1.0.0-alpha")]
    public async Task Compose_Should_Strip_Build_Metadata(string informationalVersion, string expected)
    {
        var product = UserAgentPolicy.Compose(informationalVersion);

        await Assert.That(product.ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not a version ###")]
    [Arguments("+onlymetadata")]
    public async Task Compose_Should_Omit_The_Version_Token_When_The_Version_Is_Unusable(string? informationalVersion)
    {
        var product = UserAgentPolicy.Compose(informationalVersion);

        await Assert.That(product.ToString()).IsEqualTo("OpenCode.Sdk");
    }

    [Test]
    public async Task Resolve_Should_Cache_The_Product_Token()
    {
        await Assert.That(UserAgentPolicy.Resolve()).IsSameReferenceAs(UserAgentPolicy.Resolve());
    }

    [Test]
    public async Task Resolve_Should_Read_The_Assembly_Informational_Version()
    {
        var product = UserAgentPolicy.Resolve();

        await Assert.That(product.ToString().StartsWith("OpenCode.Sdk", StringComparison.Ordinal)).IsTrue();
    }
}
