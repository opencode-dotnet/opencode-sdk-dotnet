using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceRemoteAccessTests
{
    private const string RouteDocument = "{\"remote\":{\"route\":\"kept-route\"}}";
    private const string Command = "/tools/cli";
    private static readonly string[] ExpectedArguments = ["leading", "two words", "service", "set", "remote", "true"];

    [Test]
    public async Task TurnOnAsync_Should_Use_The_Default_Cli()
    {
        var scenario = new ServiceConfigScenario(channel: null);
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, RouteDocument);
        var probes = new List<string>();
        var runner = Substitute.For<IServiceCliRunner>();
        var resolver = Resolver(path => { probes.Add(path); return true; });
        var route = await new ServiceRemoteAccess(runner, resolver, scenario.Store).TurnOnAsync(null, CancellationToken.None);
        await Assert.That(route).IsEqualTo("kept-route");
        await Assert.That(probes).IsEquivalentTo(["/tools/opencode"], CollectionOrdering.Matching);
        await runner.Received(1).RunAsync(new ResolvedExecutable("opencode", "/tools/opencode", IsBatchScript: false),
            Arg.Is<IReadOnlyList<string>>(arguments => arguments.SequenceEqual(ExpectedArguments.Skip(2))), null, CancellationToken.None);
    }

    [Test]
    public async Task TurnOnAsync_Should_Reject_An_Unresolvable_Command()
    {
        var scenario = new ServiceConfigScenario();
        scenario.Options.CliCommand = ["opencode-absent-command"];
        var runner = Substitute.For<IServiceCliRunner>();
        var failure = await Assert.That(async () => await new ServiceRemoteAccess(runner, Resolver(), scenario.Store)
            .TurnOnAsync(scenario.Options, CancellationToken.None)).Throws<OpenCodeServerException>();
        await Assert.That(failure!.Message).IsEqualTo(
            "The server command 'opencode-absent-command' was not found on PATH: 1 directory searched. " +
            "Pass the executable's full path in OpenCodeServerOptions.Command, or install the CLI package @opencode/cli.");
        await AssertNoSideEffectsAsync(runner, scenario);
    }

    [Test]
    [Arguments("null-command")]
    [Arguments("empty-command")]
    [Arguments("blank-command")]
    [Arguments("nul-command")]
    [Arguments("blank-name")]
    [Arguments("nul-name")]
    [Arguments("null-value")]
    [Arguments("nul-value")]
    [Arguments("channel")]
    [Arguments("version")]
    public async Task TurnOnAsync_Should_Refuse_Invalid_Options_Before_Resolution(string kind)
    {
        var scenario = new ServiceConfigScenario();
        var options = scenario.Options;
        options.CliCommand = kind switch
        {
            "null-command" => null!,
            "empty-command" => [],
            "blank-command" => [Command, " "],
            "nul-command" => [Command, "x\0y"],
            _ => [Command],
        };
        if (kind.Contains("name", StringComparison.Ordinal) || kind.Contains("value", StringComparison.Ordinal))
        {
            // Deliberately invalid public collection values exercise boundary validation.
            options.Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [kind switch { "blank-name" => " ", "nul-name" => "x\0y", _ => "ENTRY" }] =
                    kind switch { "null-value" => null!, "nul-value" => "x\0y", _ => "value" },
            };
        }
        options.Channel = kind == "channel" ? " " : "local";
        options.InstalledVersion = kind == "version" ? " " : null;
        var runner = Substitute.For<IServiceCliRunner>();
        var failure = await Assert.That(async () => await new ServiceRemoteAccess(runner, Resolver(), scenario.Store)
            .TurnOnAsync(options, CancellationToken.None)).Throws<ArgumentException>();
        var reason = kind switch
        {
            "channel" => "Channel cannot be blank; leave it null to use the shared release registration.",
            "version" => "InstalledVersion cannot be blank; leave it null to migrate by channel prefix only.",
            _ when kind.EndsWith("command", StringComparison.Ordinal) => "CliCommand needs an executable and nonblank entries without NUL.",
            _ => "Environment keys cannot be blank, values cannot be null, and neither can contain NUL.",
        };
        await Assert.That(failure!.ParamName).IsEqualTo("options");
        await Assert.That(failure.Message).IsEqualTo(new ArgumentException("OpenCodeServiceConfigOptions." + reason, failure.ParamName).Message);
        await AssertNoSideEffectsAsync(runner, scenario);
    }

    [Test]
    public async Task TurnOnAsync_Should_Run_The_Cli_Before_Reading_The_Route()
    {
        var scenario = new ServiceConfigScenario();
        var runner = Substitute.For<IServiceCliRunner>();
        using var caller = new CancellationTokenSource();
        scenario.Options.CliCommand = [Command, "leading", "two words"];
        scenario.Options.Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["ENTRY"] = "checked" };
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, scenario.Initial("repeated-member")!);
        OnRun(runner, async () =>
        {
            await Assert.That(scenario.Calls).IsEmpty();
            scenario.Events.Add("cli");
            await scenario.Adapter.WriteOwnerOnlyAsync(scenario.Paths.ConfigFile!, System.Text.Encoding.UTF8.GetBytes(RouteDocument), caller.Token);
        }, caller.Token);

        var result = await OpenCodeServiceConfiguration.EnableRemoteWithSeamsAsync(
            scenario.Options, new ServiceRemoteAccess(runner, Resolver(), scenario.Store), caller.Token);

        await Assert.That(result).IsEqualTo("kept-route");
        await Assert.That(scenario.Events).IsEquivalentTo(["cli", "read"], CollectionOrdering.Matching);
        await Assert.That(scenario.Calls).IsEquivalentTo(
            ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile], CollectionOrdering.Matching);
        await runner.Received(1).RunAsync(Arg.Is<ResolvedExecutable>(value => value.Path == Command),
            Arg.Is<IReadOnlyList<string>>(value => value.SequenceEqual(ExpectedArguments)),
            Arg.Is<IReadOnlyDictionary<string, string>>(value => value.Count == 1 && value["ENTRY"] == "checked"), caller.Token);
        await Assert.That(scenario.Stops).IsEmpty();
    }

    [Test]
    [Arguments("absent", false, null)]
    [Arguments("empty", false, null)]
    [Arguments("neither-shape", false, null)]
    [Arguments("repeated-member", false, null)]
    [Arguments("surrogate-route", false, null)]
    [Arguments("legacy-on", false, "0011223344556677")]
    [Arguments("legacy-off", false, null)]
    [Arguments("stored-route-and-env-route", false, "kept-route")]
    [Arguments("stored-route-and-env-route", true, "kept-route")]
    [Arguments("repeated-member", true, null)]
    [Arguments("legacy-on", true, null)]
    [Arguments("unreadable", false, null)]
    public async Task ReadRouteAsync_Should_Follow_The_Shared_Read_Result(string name, bool donor, string? route)
    {
        var scenario = new ServiceConfigScenario(donor ? "beta" : "local");
        var initial = scenario.Initial(name == "unreadable" ? "stored-route-and-env-route" : name);
        var path = donor ? scenario.Paths.LegacyConfigFile! : scenario.Paths.ConfigFile!;
        if (initial is not null)
        {
            await scenario.SeedAsync(path, initial);
        }
        if (name == "unreadable")
        {
            scenario.FileSystem.TryReadAllBytesAsync(path, Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(null));
        }
        var inventory = scenario.Inventory();
        using var caller = new CancellationTokenSource();
        var runner = Substitute.For<IServiceCliRunner>();
        scenario.Options.CliCommand = [Command];
        Task<string>[] tasks = [new ServiceRemoteAccess(runner, Resolver(), scenario.Store).TurnOnAsync(scenario.Options, caller.Token)];
        if (route is null)
        {
            var failure = await Assert.That(async () => await Task.WhenAll(tasks)).Throws<OpenCodeServerException>();
            await Assert.That(failure!.Message).IsEqualTo(
                $"The CLI reported success, but service config '{scenario.Paths.ConfigFile}' holds no remote route the SDK can read. " +
                "CliCommand, Channel and Environment may name different configs.");
        }
        else
        {
            await Assert.That((await Task.WhenAll(tasks))[0]).IsEqualTo(route);
        }
        IReadOnlyList<string> expected = ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile];
        if (donor)
        {
            expected = name == "stored-route-and-env-route" ? scenario.MigrationCalls : [.. scenario.MigrationCalls.Take(3)];
            if (route is not null)
            {
                await Assert.That(await scenario.ReadTextAsync(scenario.Paths.ConfigFile!)).IsEqualTo(initial);
            }
        }
        else if (name is "legacy-on" or "legacy-off")
        {
            expected = [.. expected, .. scenario.WriteCalls];
            await Assert.That(await scenario.ReadTextAsync(path)).Contains(name == "legacy-on" ? "\"route\": \"0011223344556677\"" : "\"hostname\": \"legacy-off\"");
        }
        else
        {
            if (name is "absent" or "unreadable")
            {
                expected = [.. expected, "Exists:" + path];
            }
            if (initial is not null)
            {
                await scenario.AssertUnchangedAsync(path, initial, inventory);
            }
        }
        await Assert.That(scenario.Calls).IsEquivalentTo(expected, CollectionOrdering.Matching);
        foreach (var call in scenario.FileSystem.ReceivedCalls().Where(static call => call.GetMethodInfo().Name == "TryReadAllBytesAsync"))
        {
            await Assert.That(call.GetArguments()[1]).IsEqualTo(caller.Token);
        }
        await Assert.That(scenario.Stops).IsEmpty();
    }

    [Test]
    public async Task TurnOnAsync_Should_Propagate_A_Runner_Failure_Without_Reading()
    {
        var scenario = new ServiceConfigScenario();
        scenario.Options.CliCommand = [Command];
        var runner = Substitute.For<IServiceCliRunner>();
        var failure = new OpenCodeServerException("synthetic CLI failure");
        runner.RunAsync(Arg.Any<ResolvedExecutable>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        var thrown = await Assert.That(async () => await new ServiceRemoteAccess(runner, Resolver(), scenario.Store)
            .TurnOnAsync(scenario.Options, CancellationToken.None)).Throws<OpenCodeServerException>();
        await Assert.That(thrown).IsSameReferenceAs(failure);
        await Assert.That(scenario.Calls).IsEmpty();
    }

    [Test]
    public async Task TurnOnAsync_Should_Snapshot_The_Collections_And_Selection_Before_Resolution()
    {
        var scenario = new ServiceConfigScenario();
        var command = new List<string> { "cli", "checked" };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["ENTRY"] = "checked" };
        scenario.Options.CliCommand = command;
        scenario.Options.Environment = environment;
        var runner = Substitute.For<IServiceCliRunner>();
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, RouteDocument);
        var resolver = Resolver(_ =>
        {
            command[1] = "changed";
            environment["ENTRY"] = "changed";
            scenario.Options.Channel = "changed";
            return true;
        });
        var route = await new ServiceRemoteAccess(runner, resolver, scenario.Store).TurnOnAsync(scenario.Options, CancellationToken.None);
        await Assert.That(route).IsEqualTo("kept-route");
        await runner.Received(1).RunAsync(Arg.Any<ResolvedExecutable>(),
            Arg.Is<IReadOnlyList<string>>(args => args[0] == "checked"),
            Arg.Is<IReadOnlyDictionary<string, string>>(entries => entries["ENTRY"] == "checked"), CancellationToken.None);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadRouteAsync_Should_Preserve_Cancellation_After_Cli_Success(bool cancelBeforeRead)
    {
        var scenario = new ServiceConfigScenario();
        using var caller = new CancellationTokenSource();
        var failure = new OperationCanceledException(caller.Token);
        scenario.FileSystem.TryReadAllBytesAsync(Arg.Any<string>(), caller.Token).Returns(Task.FromException<byte[]?>(failure));
        var runner = Substitute.For<IServiceCliRunner>();
        OnRun(runner, async () =>
        {
            if (cancelBeforeRead)
            {
                await caller.CancelOnWorkerAsync();
            }
        }, caller.Token);
        scenario.Options.CliCommand = [Command];
        var thrown = await Assert.That(async () => await new ServiceRemoteAccess(runner, Resolver(), scenario.Store)
            .TurnOnAsync(scenario.Options, caller.Token)).Throws<OperationCanceledException>();
        await Assert.That(thrown!.CancellationToken).IsEqualTo(caller.Token);
        if (!cancelBeforeRead)
        {
            await Assert.That(thrown).IsSameReferenceAs(failure);
        }
        await Assert.That(scenario.FileSystem.ReceivedCalls().Count()).IsEqualTo(cancelBeforeRead ? 0 : 1);
    }

    private static async Task AssertNoSideEffectsAsync(IServiceCliRunner runner, ServiceConfigScenario scenario)
    {
        await Assert.That(runner.ReceivedCalls()).IsEmpty();
        await Assert.That(scenario.Calls).IsEmpty();
        await Assert.That(scenario.Environment.ReceivedCalls()).IsEmpty();
    }

    private static void OnRun(IServiceCliRunner runner, Func<Task> action, CancellationToken token) =>
        runner.RunAsync(Arg.Any<ResolvedExecutable>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), token).Returns(_ => action());

    private static ExecutableResolver Resolver(Func<string, bool>? exists = null) => new(new ExecutableSearchEnvironment
    {
        IsWindows = false,
        SearchPath = "/tools",
        SearchExtensions = null,
        CurrentDirectory = "/work",
        FileExists = exists ?? (static _ => false),
    });
}
