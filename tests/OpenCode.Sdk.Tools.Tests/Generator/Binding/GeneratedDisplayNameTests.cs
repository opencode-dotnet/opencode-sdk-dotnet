using OpenCode.Sdk.Tools.Generator.Binding;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class GeneratedDisplayNameTests
{
    [Test]
    public async Task BrandSpellings_Should_Include_Every_Identifier_Brand()
    {
        foreach (var brand in CSharpNamePolicy.BrandSpellings.Values)
        {
            await Assert.That(GeneratedDisplayName.BrandSpellings.Select(static spelling => spelling.ToUpperInvariant())).Contains(brand.ToUpperInvariant());
        }
    }

    [Test]
    [Arguments("ExampleItem", "example item")]
    [Arguments("CredentialOAuth", "credential OAuth")]
    [Arguments("UnknownOpenCodeError", "unknown OpenCode error")]
    [Arguments("McpMCP", "MCP")]
    [Arguments("oauthOpenCodeMcp", "OAuth OpenCode MCP")]
    [Arguments("OpenCodec", "open codec")]
    [Arguments("OpenCodes", "open codes")]
    [Arguments("Mcps", "mcps")]
    [Arguments("XOAuth", "xo auth")]
    [Arguments("OAuth2Token", "OAuth2 token")]
    [Arguments("OAuth_2Token", "OAuth 2 token")]
    [Arguments("V1Status", "V1 status")]
    [Arguments("DurationP50", "duration P50")]
    [Arguments("MaxBase64Bytes", "max Base64 bytes")]
    [Arguments("_V1_Status_", "V1 status")]
    [Arguments("V_1Status", "v 1 status")]
    [Arguments("VcsFileStatusStatus", "vcs file status")]
    public async Task Of_Should_Render_Identifier_Prose(string name, string expected) =>
        await Assert.That(GeneratedDisplayName.Of(name)).IsEqualTo(expected);

    [Test]
    [Arguments("userName", "a user name")]
    [Arguments("username", "an username")]
    [Arguments("HourLimit", "an hour limit")]
    [Arguments("hourglass", "a hourglass")]
    [Arguments("McpServer", "an MCP server")]
    [Arguments("OAuthToken", "an OAuth token")]
    [Arguments("OpenCodeError", "an OpenCode error")]
    [Arguments("UnionValue", "a union value")]
    public async Task WithIndefiniteArticle_Should_Use_Only_Exact_First_Word_Exceptions(string name, string expected) =>
        await Assert.That(GeneratedDisplayName.WithIndefiniteArticle(name)).IsEqualTo(expected);

    [Test]
    [Arguments("user users union unions unit units unicode unique universal university one", "a")]
    [Arguments("heir heirs honest honor honors hour hours MCP", "an")]
    [Arguments("apple event item", "an")]
    [Arguments("details", "a")]
    public async Task WithIndefiniteArticle_Should_Render_Each_Listed_Word(string names, string article)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names.Split(' '))
        {
            await Assert.That(GeneratedDisplayName.WithIndefiniteArticle(name)).IsEqualTo($"{article} {name}");
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Of_Should_Reject_Blank_Identifiers(string? name) =>
        await Assert.That(() => GeneratedDisplayName.Of(name!)).Throws<ArgumentException>();
}
