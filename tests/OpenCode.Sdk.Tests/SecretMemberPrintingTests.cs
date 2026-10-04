using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal.Serialization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// A generated model whose member upstream's HTTP recorder redacts, or a curation row marks,
/// prints that member as upstream's own marker (ADR-0028): logging a configuration, a request, or
/// a union holding one never writes the secret. Presence stays visible; everything else prints as
/// the compiler would print it. A union whose known arms carry such a member prints the payload
/// of an arm this pin does not know as the marker too, since it may carry the same secret.
/// </summary>
public sealed class SecretMemberPrintingTests
{
    private const string Secret = "s3cr3t-client-value";

    private readonly FixtureLoader _fixtures = new();

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

    [Test]
    public async Task A_Curated_Config_Provider_Header_Map_Should_Be_Masked()
    {
        var provider = new ConfigProvider
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer " + Secret },
        };

        await Assert.That(provider.ToString()).Contains("Headers = [REDACTED]");
    }

    [Test]
    public async Task A_Curated_Session_Environment_Map_Should_Be_Masked()
    {
        var request = new SessionEnvironmentRequest
        {
            Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENAI_API_KEY"] = Secret },
        };

        await Assert.That(request.ToString()).IsEqualTo("SessionEnvironmentRequest { Variables = [REDACTED] }");
    }

    [Test]
    public async Task UnknownCredentialValue_Should_Print_Its_Marker_With_The_Payload_Masked()
    {
        var value = Deserialize("Serialization.unknown-credential-value.json", OpenCodeJsonContext.Default.ICredentialValue);

        await Assert.That(value).IsTypeOf<UnknownCredentialValue>();
        await Assert.That(value.ToString()).IsEqualTo("UnknownCredentialValue { Type = jwt, Payload = [REDACTED] }");
    }

    [Test]
    public async Task UnknownMcp_Should_Print_Its_Marker_With_The_Payload_Masked()
    {
        var value = Deserialize("Serialization.unknown-mcp.json", OpenCodeJsonContext.Default.IMcp);

        await Assert.That(value).IsTypeOf<UnknownMcp>();
        await Assert.That(value.ToString()).IsEqualTo("UnknownMcp { Type = sse, Payload = [REDACTED] }");
    }

    /// <summary>A config entry reaches masked members through the document it holds (MCP servers, providers, agents, formatters, and LSP servers).</summary>
    [Test]
    public async Task UnknownConfigEntry_Should_Print_Its_Marker_With_The_Payload_Masked()
    {
        var value = Deserialize("Serialization.unknown-config-entry.json", OpenCodeJsonContext.Default.IConfigEntry);

        await Assert.That(value).IsTypeOf<UnknownConfigEntry>();
        await Assert.That(value.ToString()).IsEqualTo("UnknownConfigEntry { Type = remote, Payload = [REDACTED] }");
    }

    [Test]
    public async Task McpRemoteConfigOauth_Should_Print_An_Unknown_Token_Masked()
    {
        using var document = JsonDocument.Parse(_fixtures.LoadJson("Serialization.unknown-mcp-oauth-token.json"));

        var oauth = McpRemoteConfigOauth.FromUnknown(document.RootElement);

        await Assert.That(oauth.ToString()).IsEqualTo("McpRemoteConfigOauth { Kind = Unknown, Unknown = [REDACTED] }");
    }

    [Test]
    public async Task A_Credential_Entry_Should_Not_Print_The_Payload_Of_An_Unknown_Value()
    {
        var value = (UnknownCredentialValue)Deserialize("Serialization.unknown-credential-value.json", OpenCodeJsonContext.Default.ICredentialValue);
        var entry = new CredentialEntry { Id = "cred_1", IntegrationId = "vault", Label = "work", Active = true, Value = value };

        await Assert.That(entry.ToString()).DoesNotContain(value.Payload.GetProperty("token").GetString()!);
        await Assert.That(entry.ToString()).Contains("Value = UnknownCredentialValue { Type = jwt, Payload = [REDACTED] }");
    }

    /// <summary>Masking is the printed form only: the carrier still writes back the document it preserved.</summary>
    [Test]
    public async Task UnknownCredentialValue_Should_Still_Serialize_Its_Payload()
    {
        var json = _fixtures.LoadJson("Serialization.unknown-credential-value.json");
        var value = JsonSerializer.Deserialize(json, OpenCodeJsonContext.Default.ICredentialValue)!;

        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(value, OpenCodeJsonContext.Default.ICredentialValue));
        await Assert.That(JsonElement.DeepEquals(expected.RootElement, actual.RootElement)).IsTrue();
    }

    /// <summary>No known MCP status arm carries a secret, so its unknown carrier keeps the compiler's print, payload included.</summary>
    [Test]
    public async Task UnknownMcpStatus_Should_Still_Print_Its_Payload()
    {
        var value = (UnknownMcpStatus)Deserialize("Serialization.unknown-mcp-status.json", OpenCodeJsonContext.Default.IMcpStatus);

        await Assert.That(value.ToString()).IsEqualTo($"UnknownMcpStatus {{ Status = throttled, Payload = {value.Payload.GetRawText()} }}");
    }

    private T Deserialize<T>(string fixture, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(_fixtures.LoadJson(fixture), typeInfo)
        ?? throw new InvalidOperationException($"Fixture '{fixture}' materialized null.");
}
