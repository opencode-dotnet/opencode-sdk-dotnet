using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Config;

public sealed class ConfigClientContractTests
{
    [Test]
    public async Task GetShellsAsync_Should_Return_The_Bare_Typed_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.ConfigShells);

        var response = await scenario.Client.Config.GetShellsAsync();

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Shells.Count).IsEqualTo(2);
        await Assert.That(response.Shells[0].Name).IsEqualTo("zsh");
        await Assert.That(response.Shells[0].Path).IsEqualTo("/bin/zsh");
        await Assert.That(response.Shells[0].Acceptable).IsTrue();
        await Assert.That(response.Shells[1].Acceptable).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/config/shell"));
    }

    [Test]
    [Arguments("pwsh")]
    [Arguments(null)]
    public async Task UpdateConfigAsync_Should_Send_The_Required_Nullable_Shell(string? shell)
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Experimental.UpdateConfigAsync(
            new ExperimentalConfigUpdateRequest { Shell = shell, });

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("PATCH");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/experimental/config"));
        using var body = JsonDocument.Parse(request.Body!);
        await Assert.That(body.RootElement.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(body.RootElement.GetProperty("shell").GetString()).IsEqualTo(shell);
    }
}
