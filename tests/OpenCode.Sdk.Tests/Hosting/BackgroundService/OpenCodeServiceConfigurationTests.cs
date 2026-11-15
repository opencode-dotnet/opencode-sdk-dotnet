using System.Text;
using System.Text.Json;
using NSubstitute;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService;

public sealed class OpenCodeServiceConfigurationTests
{
    [Test]
    public async Task ReadAsync_Should_Match_The_Upstream_Get_View()
    {
        using var corpus = JsonDocument.Parse(new FixtureLoader().LoadJson("BackgroundService.service-config-corpus.json"));
        using var verdicts = JsonDocument.Parse(new FixtureLoader().LoadJson("BackgroundService.service-config-expected.json"));
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var verdict = verdicts.RootElement.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
            await Assert.That(verdict.GetProperty("outcome").GetString()).IsEqualTo("ok");
            var scenario = new ServiceConfigScenario();
            var initial = row.GetProperty("initial").GetString();
            if (initial is not null)
            {
                await scenario.SeedAsync(scenario.Paths.ConfigFile!, initial);
            }

            var inventory = scenario.Inventory();
            var config = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, CancellationToken.None);

            using var output = JsonDocument.Parse(verdict.GetProperty("output").GetString()!);
            await AssertViewAsync(config, row.TryGetProperty("divergence", out _) ? default : output.RootElement);
            await Assert.That(scenario.Inventory()).IsEquivalentTo(inventory, CollectionOrdering.Matching).Because(name);
            var retained = await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, CancellationToken.None);
            if (initial is null)
            {
                await Assert.That(retained).IsNull();
            }
            else
            {
                await Assert.That(retained).IsEquivalentTo(Encoding.UTF8.GetBytes(initial), CollectionOrdering.Matching).Because(name);
            }

            string[] calls = initial is null
                ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile]
                : ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile];
            await Assert.That(scenario.Calls).IsEquivalentTo(calls, CollectionOrdering.Matching).Because(name);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReadAsync_Should_Refuse_Blank_Options_Before_Using_Any_Seam(bool channel)
    {
        var scenario = new ServiceConfigScenario();
        scenario.Options.Channel = channel ? " " : null;
        scenario.Options.InstalledVersion = channel ? null : "\t";

        var failure = await Assert.That(async () =>
        {
            _ = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, CancellationToken.None);
        }).Throws<ArgumentException>();

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.ParamName).IsEqualTo("options");
        await Assert.That(failure.Message).StartsWith("OpenCodeServiceConfigOptions.");
        await Assert.That(scenario.FileSystem.ReceivedCalls()).IsEmpty();
        await Assert.That(scenario.Environment.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(null, "service.json", 4096)]
    [Arguments("local", "service-local.json", 5123)]
    public async Task ReadAsync_Should_Read_The_Selected_Channel_Config(string? channel, string fileName, int port)
    {
        var scenario = new ServiceConfigScenario(channel);
        var path = scenario.Files.Path.Combine(scenario.Root, "config", "opencode", fileName);
        await scenario.SeedAsync(path, scenario.Initial(channel is null ? "unknown-member" : "all-members")!);

        var config = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(channel is null ? null : scenario.Options,
            scenario.Store, CancellationToken.None);

        await Assert.That(config.Port).IsEqualTo(port);
        await Assert.That(scenario.Calls).IsEquivalentTo(["TryReadAllBytesAsync:" + path], CollectionOrdering.Matching);
    }

    private static async Task AssertViewAsync(OpenCodeServiceConfig config, JsonElement expected)
    {
        await Assert.That(config.Disabled).IsEqualTo(Member(expected, "disabled").ValueKind == JsonValueKind.True);
        await Assert.That(config.RemoteEnabled).IsEqualTo(Member(expected, "remote").ValueKind == JsonValueKind.Object);
        await Assert.That(config.RemoteAccepted).IsEqualTo(Member(expected, "remoteAccepted").ValueKind == JsonValueKind.True);
        var hostname = Member(expected, "hostname");
        var port = Member(expected, "port");
        await Assert.That(config.Hostname).IsEqualTo(hostname.ValueKind == JsonValueKind.String ? hostname.GetString() : null);
        await Assert.That(config.Port).IsEqualTo(port.ValueKind == JsonValueKind.Number ? port.GetInt32() : null);
        var cors = Member(expected, "cors");
        var env = Member(expected, "env");
        await Assert.That(config.CorsOrigins).IsEquivalentTo(cors.ValueKind == JsonValueKind.Array
            ? cors.EnumerateArray().Select(static item => item.GetString()!) : [], CollectionOrdering.Matching);
        await Assert.That(config.Environment).IsEquivalentTo(env.ValueKind == JsonValueKind.Object
            ? env.EnumerateObject().Select(static item => new KeyValuePair<string, string>(item.Name, item.Value.GetString()!)) : [], CollectionOrdering.Any);
    }

    private static JsonElement Member(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
}
