namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpOptionsTests
{
    [Test]
    public async Task ToString_Should_Omit_Synthetic_Url_And_Header_Credentials()
    {
        var printed = new ClientMcpOptions
        {
            Name = "app",
            Tools = [],
            Url = new Uri("https://user:password@host.test/?ticket=nonce&custom=credential"),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "header-secret" },
        }.ToString();
        await Assert.That(printed).IsEqualTo("ClientMcpOptions { Name = app }");
        foreach (var credential in new[] { "user", "password", "nonce", "credential", "header-secret" })
        {
            await Assert.That(printed).DoesNotContain(credential);
        }
    }
}
