using Microsoft.Extensions.Configuration;
using OpenCode.Playground.Hq.Features.Connection;

namespace OpenCode.Playground.Hq.Tests.Features.Connection;

public sealed class OpenCodeConnectionTests
{
    [Test]
    [Arguments(null)]
    [Arguments("not an endpoint")]
    public async Task Connect_Should_Name_The_AppHost_When_Endpoint_Is_Invalid(string? endpoint)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["OpenCode:Endpoint"] = endpoint }).Build();
        var failure = await Assert.That(() => OpenCodeConnection.Connect(configuration)).Throws<InvalidOperationException>();
        await Assert.That(failure?.Message).IsEqualTo("OpenCode:Endpoint is missing or invalid. Start HQ through its AppHost.");
    }
}
