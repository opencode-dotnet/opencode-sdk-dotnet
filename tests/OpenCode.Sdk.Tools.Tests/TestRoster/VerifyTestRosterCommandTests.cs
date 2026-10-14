using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Tests.TestRoster.Support;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.TestRoster;

public sealed class VerifyTestRosterCommandTests
{
    [Test]
    public async Task Execute_Should_Pass_And_Print_Each_Entry_With_Its_Executed_Count_When_The_Roster_Matches_The_Results()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0", "Beta.Tests/net472")
            .WithTrx("Alpha.Tests", "net10.0", total: 2001, executed: 1201)
            .WithTrx("Beta.Tests", "net472", total: 503, executed: 401)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("Alpha.Tests/net10.0");
        await Assert.That(result.Output).Contains("Beta.Tests/net472");
        // The printed figure is the executed count, never the total; the counts are distinct
        // enough that neither can appear by accident elsewhere in the output.
        await Assert.That(result.Output).Contains("1201");
        await Assert.That(result.Output).Contains("401");
        await Assert.That(result.Output).DoesNotContain("2001");
        await Assert.That(result.Output).DoesNotContain("503");
        await Assert.That(result.Output).Contains("Test roster verified");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_The_Module_When_A_Roster_Entry_Has_No_Trx()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0", "Beta.Tests/net472")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Beta.Tests/net472");
        await Assert.That(result.Output).Contains("no TRX found");
        await Assert.That(result.Output).Contains("the module did not run");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_The_Module_When_A_Trx_Is_Not_In_The_Roster()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3)
            .WithTrx("Beta.Tests", "net10.0", total: 5, executed: 5)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Beta.Tests/net10.0");
        await Assert.That(result.Output).Contains("the roster does not list");
        await Assert.That(result.Output).Contains("add it to the roster");
    }

    [Test]
    public async Task Execute_Should_Fail_When_A_Trx_Reports_A_Zero_Total()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 0, executed: 0)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Alpha.Tests/net10.0");
        await Assert.That(result.Output).Contains("total=0");
        await Assert.That(result.Output).Contains("the module ran no test");
    }

    [Test]
    public async Task Execute_Should_Fail_When_A_Trx_Reports_Zero_Executed()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 7, executed: 0)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Alpha.Tests/net10.0");
        await Assert.That(result.Output).Contains("executed=0");
        await Assert.That(result.Output).Contains("the module ran no test");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_Both_Files_When_Two_Trx_Files_Name_The_Same_Module()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3, arch: "x64")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3, arch: "arm64")
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Alpha.Tests/net10.0");
        await Assert.That(result.Output).Contains("name the same module");
        await Assert.That(result.Output).Contains("Alpha.Tests_net10.0_x64.trx");
        await Assert.That(result.Output).Contains("Alpha.Tests_net10.0_arm64.trx");
    }

    [Test]
    public async Task Execute_Should_Ignore_Subfolders_Of_The_Results_Directory()
    {
        // merged/ carries a duplicate-named zero-counter TRX: if the reader reached subfolders at
        // all, this scenario would fail as a duplicate or an empty module.
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3)
            .WithFile("merged/Alpha.Tests_net10.0_x64.trx", TrxContent.WithCounters(0, 0))
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("Test roster verified");
    }

    [Test]
    public async Task Execute_Should_Fail_When_The_Pass_Is_Not_In_The_Roster()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "nightly", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("no pass 'nightly'");
        await Assert.That(result.Output).Contains("'main'");
    }

    [Test]
    public async Task Execute_Should_Fail_When_The_Operating_System_Is_Not_In_The_Pass()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "Solaris"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("no OS 'Solaris'");
        await Assert.That(result.Output).Contains("'windows'");
    }

    [Test]
    public async Task Execute_Should_Match_The_Operating_System_Case_Insensitively()
    {
        // runner.os spells the GitHub values (Windows, Linux, macOS); the roster keys are lowercase.
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3)
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "Windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("Test roster verified");
    }

    [Test]
    public async Task Execute_Should_Fail_When_A_Trx_File_Name_Does_Not_Parse()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows", "Alpha.Tests/net10.0")
            .WithTrx("Alpha.Tests", "net10.0", total: 3, executed: 3)
            .WithFile("not-a-trx-name.trx", TrxContent.WithCounters(3, 3))
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("not-a-trx-name.trx");
        await Assert.That(result.Output).Contains("does not follow");
    }

    [Test]
    public async Task Execute_Should_Fail_When_The_Roster_File_Is_Missing()
    {
        var result = await RunAsync(new MockFileSystem(), ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("was not found");
    }

    [Test]
    public async Task Execute_Should_Refuse_A_Roster_Leg_That_Lists_No_Modules()
    {
        var fileSystem = new TestRosterScenario()
            .WithEntry("main", "windows")
            .Build();

        var result = await RunAsync(fileSystem, ["--pass", "main", "--os", "windows"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("lists no modules");
    }

    private static async Task<CommandAppResult> RunAsync(IFileSystem fileSystem, string[] passArguments)
    {
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        return await tester.RunAsync(
        [
            "verify-test-roster",
            "--roster",
            TestRosterScenario.RosterPath,
            "--results",
            TestRosterScenario.ResultsDirectory,
            .. passArguments,
        ]);
    }
}
