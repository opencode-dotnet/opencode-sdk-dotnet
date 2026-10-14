using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Shells;

public sealed class ShellsClientContractTests
{
    [Test]
    public async Task ListShellsAsync_Should_Return_The_Typed_Page_With_Its_Location()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-shell.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{payload}]"));

        var response = await scenario.Client.Shells.ListShellsAsync();

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Shells.Single().Id).IsEqualTo("sh_100");
        await Assert.That(response.Shells.Single().Status).IsEqualTo(ShellInfoStatus.Running);
        await Assert.That(response.Location.Directory).IsEqualTo("/repo");
        await Assert.That(scenario.Requests.Single().RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/shell"));
    }

    [Test]
    public async Task ListShellsAsync_Should_Compose_The_Deep_Object_Location_Query()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-shell.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{payload}]"));

        _ = await scenario.Client.Shells.ListShellsAsync(new ShellListRequest
        {
            Location = new LocationSelector { Directory = "/a b" },
        });

        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri).IsEqualTo(
            "http://localhost:4096/api/shell?location[directory]=%2Fa%20b");
    }

    [Test]
    public async Task CreateShellAsync_Should_Send_The_Body_Beside_The_Location_Query()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-shell.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(payload));

        var response = await scenario.Client.Shells.CreateShellAsync(new ShellCreateRequest
        {
            Command = "ls -la",
            Timeout = 5000,
            Location = new LocationSelector { Directory = "/repo" },
        });

        await Assert.That(response.Shell.Id).IsEqualTo("sh_100");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/shell?location[directory]=%2Frepo");
        await Assert.That(request.Body).IsEqualTo("{\"command\":\"ls -la\",\"timeout\":5000}");
    }

    [Test]
    public async Task CreateShellAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Shells.CreateShellAsync(new ShellCreateRequest
            {
                Command = "ls",
                Timeout = 1,
            }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListShellsAsync_Should_Treat_A_Missing_Location_Sibling_As_A_Protocol_Failure()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-shell.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, $"{{\"data\":[{payload}]}}");

        _ = await Assert
            .That(async () => _ = await scenario.Client.Shells.ListShellsAsync())
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    [Arguments(" ")]
    [Arguments(".")]
    [Arguments("..")]
    public async Task GetShellClient_Should_Refuse_An_Id_The_Route_Would_Refuse(string id)
    {
        using var scenario = ContractScenario.Responding();

        var exception = Assert.Throws<ArgumentException>(() => _ = scenario.Client.Shells.GetShellClient(id));

        await Assert.That(exception.ParamName).IsEqualTo("shellId");
    }
}
