using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// A generated model whose member upstream's HTTP recorder redacts, or a curation row marks,
/// prints that member as upstream's own marker (ADR-0028): logging a configuration, a request, or
/// a union holding one never writes the secret. Presence stays visible; everything else prints as
/// the compiler would print it.
/// </summary>
public sealed class SecretMemberPrintingTests
{
    private const string Secret = "s3cr3t-client-value";

    [Test]
    public async Task McpOAuthConfig_Should_Mask_The_Client_Secret()
    {
        var config = new McpOAuthConfig { ClientId = "app", ClientSecret = Secret, Scope = "read" };

        await Assert.That(config.ToString()).IsEqualTo(
            "McpOAuthConfig { ClientId = app, ClientSecret = [REDACTED], Scope = read, CallbackPort = , RedirectUri = , AuthServerMetadataUrl =  }");
    }

    [Test]
    public async Task McpOAuthConfig_Should_Print_An_Absent_Secret_Empty()
    {
        var config = new McpOAuthConfig { ClientId = "app" };

        await Assert.That(config.ToString()).Contains("ClientSecret = ,");
    }

    [Test]
    public async Task The_Union_Arm_Should_Print_The_Masked_Config()
    {
        var oauth = McpRemoteConfigOauth.FromMcpOAuthConfig(new McpOAuthConfig { ClientSecret = Secret });

        await Assert.That(oauth.ToString()).DoesNotContain(Secret);
        await Assert.That(oauth.ToString()).Contains("ClientSecret = [REDACTED]");
    }

    [Test]
    public async Task A_Curated_Credential_Should_Be_Masked()
    {
        var request = new IntegrationConnectKeyRequest { Key = Secret };

        await Assert.That(request.ToString()).DoesNotContain(Secret);
        await Assert.That(request.ToString()).Contains("Key = [REDACTED]");
    }

    [Test]
    public async Task A_Curated_Header_Map_Should_Be_Masked()
    {
        var remote = new McpRemoteConfig
        {
            Url = "https://mcp.example.test",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer " + Secret },
        };

        await Assert.That(remote.ToString()).DoesNotContain(Secret);
        await Assert.That(remote.ToString()).Contains("Headers = [REDACTED]");
    }

    [Test]
    public async Task A_Credential_Key_Should_Be_Masked()
    {
        var key = new CredentialKey { Key = Secret };

        await Assert.That(key.ToString()).DoesNotContain(Secret);
        await Assert.That(key.ToString()).Contains("Key = [REDACTED]");
    }

    [Test]
    public async Task Credential_OAuth_Tokens_Should_Be_Masked()
    {
        var oauth = new CredentialOAuth { MethodId = "device", Access = Secret, Refresh = Secret + "-refresh", Expires = 1 };

        await Assert.That(oauth.ToString()).DoesNotContain(Secret);
        await Assert.That(oauth.ToString()).Contains("Refresh = [REDACTED], Access = [REDACTED]");
        await Assert.That(oauth.ToString()).Contains("MethodId = device");
    }

    [Test]
    public async Task A_Credential_Entry_Should_Print_Its_Value_Masked()
    {
        var entry = new CredentialEntry
        {
            Id = "cred_1",
            IntegrationId = "openai",
            Label = "work",
            Active = true,
            Value = new CredentialKey { Key = Secret },
        };

        await Assert.That(entry.ToString()).DoesNotContain(Secret);
        await Assert.That(entry.ToString()).Contains("Key = [REDACTED]");
    }
}
