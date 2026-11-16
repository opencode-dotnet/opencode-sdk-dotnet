using System.Text;
using System.Text.Json;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService;

public sealed class OpenCodeServiceConfigurationTests
{
    [Test]
    public async Task GetOrCreatePasswordAsync_Should_Validate_Options_At_The_Public_Door()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();
        await AssertBlankOptionsAsync(() =>
            OpenCodeServiceConfiguration.GetOrCreatePasswordAsync(new OpenCodeServiceConfigOptions { Channel = " " }, caller.Token));
    }

    [Test]
    public async Task ReadAsync_Should_Validate_Options_At_The_Public_Door()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();
        await AssertBlankOptionsAsync(() =>
            OpenCodeServiceConfiguration.ReadAsync(new OpenCodeServiceConfigOptions { Channel = " " }, caller.Token));
    }

    private static async Task AssertBlankOptionsAsync(Func<Task> operation)
    {
        var failure = await Assert.That(operation).Throws<ArgumentException>();

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure).IsTypeOf<ArgumentException>();
        await Assert.That(failure.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task ReadAsync_Should_Forward_The_Caller_Token_To_Each_Read()
    {
        var scenario = new ServiceConfigScenario("beta");
        await scenario.SeedAsync(scenario.Paths.LegacyConfigFile!, scenario.Initial("all-members")!);
        using var caller = new CancellationTokenSource();

        var config = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, caller.Token);

        await Assert.That(config.Port).IsEqualTo(5123);
        var calls = scenario.FileSystem.ReceivedCalls().Where(static call => call.GetMethodInfo().Name == "TryReadAllBytesAsync").ToArray();
        await Assert.That(calls.Select(static call => (string)call.GetArguments()[0]!)).IsEquivalentTo(
            [scenario.Paths.ConfigFile, scenario.Paths.LegacyConfigFile, scenario.Paths.ConfigFile], CollectionOrdering.Matching);
        foreach (var call in calls)
        {
            await Assert.That(call.GetArguments()[1]).IsEqualTo(caller.Token);
        }
        var copy = scenario.FileSystem.ReceivedCalls().Single(static call => call.GetMethodInfo().Name == "TryCreateExclusiveAsync");
        await Assert.That(copy.GetArguments()[2]).IsEqualTo(caller.Token);
        await Assert.That(scenario.Calls).IsEquivalentTo(scenario.MigrationCalls, CollectionOrdering.Matching);
        await Assert.That(await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, caller.Token))
            .IsEquivalentTo(Encoding.UTF8.GetBytes(scenario.Initial("all-members")!), CollectionOrdering.Matching);
        await Assert.That(await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.LegacyConfigFile!, caller.Token))
            .IsEquivalentTo(Encoding.UTF8.GetBytes(scenario.Initial("all-members")!), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("repeated-member", false)]
    [Arguments("surrogate-hostname", false)]
    [Arguments("surrogate-route", false)]
    [Arguments("surrogate-cors", false)]
    [Arguments("deep-read", false)]
    [Arguments("legacy-repeated", false)]
    [Arguments("repeated-member", true)]
    [Arguments("surrogate-route", true)]
    public async Task ReadAsync_Should_Refuse_The_Document_Before_Any_Write(string name, bool donor)
    {
        var scenario = new ServiceConfigScenario(donor ? "beta" : "local");
        var path = donor ? scenario.Paths.LegacyConfigFile! : scenario.Paths.ConfigFile!;
        var initial = scenario.Initial(name)!;
        await scenario.SeedAsync(path, initial);
        scenario.ClearCalls();
        var inventory = scenario.Inventory();

        var failure = await Assert.That(async () =>
            await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.Message).Contains(path);
        await scenario.AssertUnchangedAsync(path, initial, inventory);
        string[] expected = donor
            ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile, "TryReadAllBytesAsync:" + path]
            : ["TryReadAllBytesAsync:" + path];
        await Assert.That(scenario.Calls).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("legacy-on", true)]
    [Arguments("legacy-off", false)]
    public async Task ReadAsync_Should_Repair_A_Legacy_Remote_With_The_Exact_Scripted_Route(string name, bool enabled)
    {
        var scenario = new ServiceConfigScenario();
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, scenario.Initial(name)!);
        scenario.ClearCalls();

        var view = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, CancellationToken.None);

        await Assert.That(view.RemoteEnabled).IsEqualTo(enabled);
        var text = await scenario.ReadTextAsync(scenario.Paths.ConfigFile!);
        await Assert.That(text).IsEqualTo(enabled
            ? "{\n  \"hostname\": \"legacy-on\",\n  \"env\": {\n    \"A\": \"1\"\n  },\n  \"remote\": {\n    \"route\": \"0011223344556677\"\n  }\n}\n"
            : "{\n  \"hostname\": \"legacy-off\",\n  \"env\": {\n    \"A\": \"1\"\n  }\n}\n");
        await Assert.That(scenario.Calls).IsEquivalentTo(
            ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, .. scenario.WriteCalls], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("unknown-member")]
    [Arguments("neither-shape")]
    [Arguments("unreadable")]
    [Arguments("legacy-only")]
    [Arguments("absent")]
    public async Task ReadAsync_Should_Leave_The_Files_Unchanged_Where_The_Cli_Does_Not_Write(string name)
    {
        var scenario = new ServiceConfigScenario(name == "legacy-only" ? "beta" : "local");
        var path = name == "legacy-only" ? scenario.Paths.LegacyConfigFile! : scenario.Paths.ConfigFile!;
        var initial = name switch
        {
            "unreadable" => scenario.Initial("all-members"),
            "legacy-only" => scenario.Initial("legacy-on"),
            _ => scenario.Initial(name),
        };
        if (initial is not null)
        {
            await scenario.SeedAsync(path, initial);
        }
        if (name == "unreadable")
        {
            scenario.FileSystem.TryReadAllBytesAsync(path, Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        }
        scenario.ClearCalls();
        var inventory = scenario.Inventory();

        var config = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, CancellationToken.None);

        using var original = JsonDocument.Parse(initial ?? "{}");
        await AssertViewAsync(config, name == "unknown-member" ? original.RootElement : default);
        await Assert.That(scenario.Inventory()).IsEquivalentTo(inventory, CollectionOrdering.Matching);
        if (initial is not null)
        {
            await scenario.AssertUnchangedAsync(path, initial, inventory);
        }
        string[] expected = name switch
        {
            "legacy-only" => ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile,
                "TryReadAllBytesAsync:" + path],
            "unreadable" or "absent" => ["TryReadAllBytesAsync:" + path, "Exists:" + path],
            _ => ["TryReadAllBytesAsync:" + path],
        };
        await Assert.That(scenario.Calls).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadAsync_Should_Propagate_The_Read_Cancellation_Instance(bool donor)
    {
        var scenario = new ServiceConfigScenario("beta");
        using var caller = new CancellationTokenSource();
        var failure = new OperationCanceledException(caller.Token);
        var path = donor ? scenario.Paths.LegacyConfigFile! : scenario.Paths.ConfigFile!;
        scenario.FileSystem.TryReadAllBytesAsync(path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]?>(failure));
        scenario.ClearCalls();

        var observed = await Assert.That(async () =>
        {
            _ = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, caller.Token);
        }).Throws<OperationCanceledException>();

        await Assert.That(observed).IsSameReferenceAs(failure);
        string[] calls = donor
            ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile, "TryReadAllBytesAsync:" + path]
            : ["TryReadAllBytesAsync:" + path];
        await Assert.That(scenario.Calls).IsEquivalentTo(calls, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Observe_Pre_Cancellation_Before_Using_Any_Seam()
    {
        var scenario = new ServiceConfigScenario();
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();

        var failure = await Assert.That(async () =>
        {
            _ = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, caller.Token);
        }).Throws<OperationCanceledException>();

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(scenario.Environment.ReceivedCalls()).IsEmpty();
        await Assert.That(scenario.FileSystem.ReceivedCalls()).IsEmpty();
    }

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
            await AssertParityRowAsync(row, verdict);
        }
    }

    private static async Task AssertParityRowAsync(JsonElement row, JsonElement verdict)
    {
        var scenario = new ServiceConfigScenario();
        var initial = row.GetProperty("initial").GetString();
        if (initial is not null)
        {
            await scenario.SeedAsync(scenario.Paths.ConfigFile!, initial);
        }
        scenario.ClearCalls();
        var inventory = scenario.Inventory();
        var command = row.GetProperty("command").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        if (row.TryGetProperty("divergence", out _))
        {
            var failure = await Assert.That(async () => await InvokeParityAsync(scenario, command, verdict)).Throws<OpenCodeServerException>();
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure.Message).Contains(scenario.Paths.ConfigFile!);
            await scenario.AssertUnchangedAsync(scenario.Paths.ConfigFile!, initial!, inventory);
            await Assert.That(scenario.Calls).IsEquivalentTo(["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile], CollectionOrdering.Matching);
            return;
        }

        await InvokeParityAsync(scenario, command, verdict);
        var retained = await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, CancellationToken.None);
        var expected = verdict.GetProperty("file").GetString();
        var actual = retained is null ? null : Encoding.UTF8.GetString(retained);
        using var original = JsonDocument.Parse(initial ?? "{}");
        if (Member(original.RootElement, "remote").ValueKind == JsonValueKind.True)
        {
            using var written = JsonDocument.Parse(actual!);
            var route = written.RootElement.GetProperty("remote").GetProperty("route").GetString()!;
            await Assert.That(route).Matches("^[0-9a-f]{16}$");
            actual = MaskGenerated(actual!, "\n  \"remote\": {\n    \"route\": ", route, "@route");
        }
        if (command.Length == 2 && command[1] == "password" && verdict.GetProperty("output").GetString() == "@password")
        {
            using var written = JsonDocument.Parse(actual!);
            var password = written.RootElement.GetProperty("password").GetString()!;
            await Assert.That(password).Matches("^[A-Za-z0-9_-]{43}$");
            actual = MaskGenerated(actual!, "\n  \"password\": ", password, "@password");
        }
        await Assert.That(actual).IsEqualTo(expected).Because(row.GetProperty("name").GetString()!);
        if (expected == initial)
        {
            await Assert.That(scenario.Inventory()).IsEquivalentTo(inventory, CollectionOrdering.Matching);
            string[] calls = initial is null
                ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile]
                : ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile];
            await Assert.That(scenario.Calls).IsEquivalentTo(calls, CollectionOrdering.Matching);
        }
    }

    private static string MaskGenerated(string text, string prefix, string secret, string marker) =>
#if NET
        text.Replace(prefix + "\"" + secret + "\"", prefix + "\"" + marker + "\"", StringComparison.Ordinal);
#else
        // Framework's Replace has no comparison overload and always replaces ordinally.
        text.Replace(prefix + "\"" + secret + "\"", prefix + "\"" + marker + "\"");
#endif

    private static async Task InvokeParityAsync(ServiceConfigScenario scenario, string[] command, JsonElement verdict)
    {
        if (command[0] == "read")
        {
            _ = await scenario.Store.ReadForWriteAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None);
        }
        else if (command.Length == 2)
        {
            var password = await OpenCodeServiceConfiguration.GetOrCreatePasswordWithSeamsAsync(
                scenario.Options, new ServiceConfigEditor(scenario.Store), CancellationToken.None);
            var expected = verdict.GetProperty("output").GetString();
            if (expected == "@password")
            {
                await Assert.That(password).Matches("^[A-Za-z0-9_-]{43}$");
                var bytes = await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, CancellationToken.None);
                using var written = JsonDocument.Parse(bytes!);
                await Assert.That(written.RootElement.GetProperty("password").GetString()).IsEqualTo(password);
            }
            else
            {
                await Assert.That(password).IsEqualTo(expected);
            }
        }
        else
        {
            var config = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, CancellationToken.None);
            using var output = JsonDocument.Parse(verdict.GetProperty("output").GetString()!);
            await AssertViewAsync(config, output.RootElement);
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
