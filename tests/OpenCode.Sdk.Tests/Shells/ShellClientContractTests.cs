using System.Net;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Shells;

public sealed class ShellClientContractTests
{
    [Test]
    public async Task GetAsync_Should_Return_The_Typed_Shell_With_Its_Location()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-shell.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(payload));

        var response = await scenario.Client.Shells.GetShellClient("sh_100").GetAsync();

        await Assert.That(response.Shell.Id).IsEqualTo("sh_100");
        await Assert.That(response.Shell.Time.Started).IsEqualTo(1755200000);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/shell/sh_100"));
    }

    [Test]
    public async Task RemoveAsync_Should_Treat_The_204_As_A_Bodiless_Success()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Shells.GetShellClient("sh_100").RemoveAsync();

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.RawBody).IsNull();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/shell/sh_100"));
    }

    [Test]
    public async Task RemoveAsync_Should_Ignore_A_Body_On_The_Declared_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, "{}");

        var response = await scenario.Client.Shells.GetShellClient("sh_100").RemoveAsync();

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
    }

    [Test]
    public async Task RemoveAsync_Should_Treat_A_200_As_An_Undeclared_Success()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, "{}");

        _ = await Assert
            .That(async () => _ = await scenario.Client.Shells.GetShellClient("sh_100").RemoveAsync())
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task GetOutputAsync_Should_Return_The_Typed_Output_Page_With_Its_Location()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK, WireBodyData.LocationEnvelope(WireBodyData.ShellOutputPage));

        var response = await scenario.Client.Shells.GetShellClient("sh_100").GetOutputAsync();

        await Assert.That(response.Output.Output).IsEqualTo("build ok\n");
        await Assert.That(response.Output.Cursor).IsEqualTo(24L);
        await Assert.That(response.Output.Size).IsEqualTo(96L);
        await Assert.That(response.Output.Truncated).IsTrue();
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/shell/sh_100/output"));
    }

    [Test]
    public async Task GetOutputAsync_Should_Send_The_Cursor_And_Limit_Exactly_As_Given()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK, WireBodyData.LocationEnvelope(WireBodyData.ShellOutputPage));

        _ = await scenario.Client.Shells.GetShellClient("sh_100").GetOutputAsync(new ShellOutputRequest
        {
            Location = new LocationSelector { Directory = "/repo" },
            Cursor = 1024,
            Limit = 4096,
        });

        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/shell/sh_100/output?location[directory]=%2Frepo&cursor=1024&limit=4096");
    }
}
