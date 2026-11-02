using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Tests.Support;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tools.Tests.Commands;

public sealed class CompareBenchmarksCommandTests
{
    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Execute_Should_Refuse_Na_Cases_In_Reader_Order_Without_Writing_Csv(bool beforeNa, bool afterNa)
    {
        const string outputPath = "comparison.csv";
        var fileSystem = BenchmarkRunData.CreateComparisonStore();
        var report = new FixtureLoader().Load("Benchmarks.na-report-full.json");
        if (beforeNa)
        {
            fileSystem.Initialize().With(new FileDescription($"{BenchmarkRunData.BeforeDirectory}/results/na-report-full.json", report));
        }

        if (afterNa)
        {
            fileSystem.Initialize().With(new FileDescription($"{BenchmarkRunData.AfterDirectory}/results/na-report-full.json", report));
        }

        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole>(_ => new TestConsole().Width(200));
        });
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings { TrimConsoleOutput = false });
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(
            ["compare-benchmarks", BenchmarkRunData.BeforeDirectory, BenchmarkRunData.AfterDirectory, "--output", outputPath]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).IsEqualTo(
            $"The runs contain failed (NA) benchmark cases ({(beforeNa ? "3" : "0")} before, {(afterNa ? "3" : "0")} after).\n"
            + (beforeNa ? "before NA: Info/GetInfoAsync [Fixture=info] (Dry)\n"
                + "before NA: Info/ExecuteWithoutAdapterAsync [Fixture=info] (Dry)\n"
                + "before NA: Info/AdaptSuccess [Fixture=info] (Dry)\n" : string.Empty)
            + (afterNa ? "after NA: Info/GetInfoAsync [Fixture=info] (Dry)\n"
                + "after NA: Info/ExecuteWithoutAdapterAsync [Fixture=info] (Dry)\n"
                + "after NA: Info/AdaptSuccess [Fixture=info] (Dry)\n" : string.Empty));
        await Assert.That(fileSystem.Directory.GetFiles(".", "*.csv", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task Execute_Should_Refuse_A_Missing_Run_Through_The_Shared_Handler()
    {
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(new MockFileSystem());
            services.AddSingleton<IAnsiConsole, TestConsole>();
        });
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings { TrimConsoleOutput = false });
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["compare-benchmarks", "missing[run]", "after"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).IsEqualTo("Refused: Benchmark run directory 'missing[run]' does not exist.\n");
    }

    [Test]
    public async Task Execute_Should_Compare_Runs_And_Write_The_Csv_Extract()
    {
        const string outputPath = ".benchmarks/health-comparison.csv";
        var fileSystem = BenchmarkRunData.CreateComparisonStore();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(
        [
            "compare-benchmarks",
            BenchmarkRunData.BeforeDirectory,
            BenchmarkRunData.AfterDirectory,
            "--output",
            outputPath,
        ]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var csv = await fileSystem.File.ReadAllTextAsync(outputPath, CancellationToken.None);
        await Assert.That(csv).StartsWith(
            "\"Case\",\"Runtime\",\"Status\",\"WireBytes\",\"WireItems\",\"PayloadBytesPerItem\",\"AllocBefore\",\"AllocAfter\",\"AllocDelta\",\"TimeRatio\",\"MedianNanoseconds\"\n");
        await Assert.That(csv).Contains(
            "\"Health/GetHealthAsync [Fixture=health]\",\".NET 10.0\",\"Matched\",\"\",\"\",\"\",\"2104\",\"2376\",\"272\",\"0.98\",\"\"\n");
        await Assert.That(csv).Contains(
            "\"Health/ExecuteWithoutAdapterAsync [Fixture=health]\",\".NET Framework 4.7.2\",\"Matched\",\"\",\"\",\"\",\"6142\",\"4413\",\"-1729\",\"0.37\",\"\"\n");

        // The after run's new rung has no baseline yet — it must still land with exact figures rather
        // than vanish from the durable CSV (ROADMAP.md's compare-benchmarks completeness requirement).
        await Assert.That(csv).Contains(
            "\"Health/GetVersionAsync [Fixture=health]\",\".NET 10.0\",\"AfterOnly\",\"\",\"\",\"\",\"\",\"96\",\"\",\"\",\"645.5\"\n");

        // Both runs carry the same two runtime legs, so the leg-mismatch warning must stay silent.
        await Assert.That(result.Output).DoesNotContain("warning:");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_Both_Runtime_Label_Sets_When_The_Runs_Share_No_Cases()
    {
        var fileSystem = new MockFileSystem();
        var loader = new FixtureLoader();
        fileSystem.Initialize().With(
            new FileDescription(BenchmarkRunData.BeforeReportPath, loader.Load(BenchmarkRunData.BeforeFixture)),
            new FileDescription(
                BenchmarkRunData.AfterReportPath,
                loader.Load("Benchmarks.single-job-report-full.json")));
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(
        [
            "compare-benchmarks",
            BenchmarkRunData.BeforeDirectory,
            BenchmarkRunData.AfterDirectory,
        ]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("The runs share no benchmark cases");
        await Assert.That(result.Output).Contains("before runtimes: '.NET 10.0', '.NET Framework 4.7.2'");
        await Assert.That(result.Output).Contains("after runtimes: 'DefaultJob'");
        await Assert.That(result.Output).Contains("--runtimes");
    }

    [Test]
    public async Task Execute_Should_Refuse_Zero_Overlap_Before_Na_Cases_Without_Writing_Csv()
    {
        const string outputPath = "comparison.csv";
        var fileSystem = new MockFileSystem();
        var loader = new FixtureLoader();
        fileSystem.Initialize().With(
            new FileDescription(BenchmarkRunData.BeforeReportPath, loader.Load(BenchmarkRunData.BeforeFixture)),
            new FileDescription(BenchmarkRunData.AfterReportPath, loader.Load("Benchmarks.na-report-full.json")));
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole>(_ => new TestConsole().Width(200));
        });
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings { TrimConsoleOutput = false });
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(
            ["compare-benchmarks", BenchmarkRunData.BeforeDirectory, BenchmarkRunData.AfterDirectory, "--output", outputPath]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).IsEqualTo(
            "The runs share no benchmark cases (4 before, 3 after).\n"
            + "before runtimes: '.NET 10.0', '.NET Framework 4.7.2'\n"
            + "after runtimes: 'Dry'\n"
            + "Cases join on (case, runtime). A job-named leg such as 'DefaultJob' means that run was launched "
            + "without --runtimes; relaunch it with --runtimes net10.0 net472 to make it joinable.\n");
        await Assert.That(fileSystem.Directory.GetFiles(".", "*.csv", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task Execute_Should_Print_Both_Runs_Identity_And_Still_Compare_When_They_Differ()
    {
        var loader = new FixtureLoader();
        string[] versions = ["0.2", "0.1", "0.2"];
        var after = versions.Select((version, index) =>
        {
            var report = JsonNode.Parse(loader.Load(BenchmarkRunData.AfterFixture))!; // The fixture is a JSON object.
            report["HostEnvironmentInfo"]!["BenchmarkDotNetVersion"] = version;
            report["Benchmarks"] = new JsonArray(report["Benchmarks"]![index]!.DeepClone());
            return report.ToJsonString();
        }).ToArray();

        var result = await CompareAsync(loader.Load("Benchmarks.net10-only-report-full.json"), after);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).StartsWith(
            "before BenchmarkDotNet: 0.15.8\nbefore host: unknown; unknown; SDK unknown\n"
            + "before jobs: .NET 10.0(Runtime=.NET 10.0, Toolchain=net10.0)\n"
            + "after BenchmarkDotNet: 0.1 | 0.2\nafter host: unknown; unknown; SDK unknown\n"
            + "after jobs: .NET 10.0(Runtime=.NET 10.0, Toolchain=net10.0) | .NET Framework 4.7.2(Runtime=.NET Framework 4.7.2, Toolchain=net472)\n");
    }

    [Test]
    public async Task Execute_Should_Print_Unknown_For_An_Export_Without_Host_Information()
    {
        var report = new FixtureLoader().Load("Benchmarks.single-job-report-full.json");
        var result = await CompareAsync(report, report);

        await Assert.That(result.Output.Split('\n')).Contains("before BenchmarkDotNet: unknown");
        await Assert.That(result.Output.Split('\n')).Contains("before host: unknown; unknown; SDK unknown");
        await Assert.That(result.Output.Split('\n')).Contains("before jobs: DefaultJob");
    }

    [Test]
    public async Task Execute_Should_Keep_Populated_Host_Jobs_And_Fixture_Warning_Lines_Unwrapped()
    {
        const string os = "Microsoft Windows 11 Enterprise [10.0.26100]";
        const string cpu = "Intel Core i9-14900K Processor, 24 cores";
        const string cli = "10.0.100-preview.7.25380.108";
        const string job = "Job-LVKMJU(Runtime=.NET 10.0, Toolchain=net10.0, IterationCount=15, LaunchCount=1, WarmupCount=10)";
        var before = JsonNode.Parse(new FixtureLoader().Load("Benchmarks.wire-metrics-report-full.json"))!; // The fixture is a JSON object.
        before["HostEnvironmentInfo"]!["OsVersion"] = os;
        before["HostEnvironmentInfo"]!["ProcessorName"] = cpu;
        before["HostEnvironmentInfo"]!["DotNetCliVersion"] = cli;
        before["Benchmarks"]![1]!["DisplayInfo"] = $"HealthBenchmarks.ComposeRequest: {job}";
        var report = before.ToJsonString();
        using var console = new TestConsole();
        console.Width(80);

        var result = await CompareAsync(console, report, WithMetric(report, "WireBytes", 98));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var lines = result.Output.Split('\n');
        await Assert.That(lines).Contains($"before host: {os}; {cpu}; SDK {cli}");
        await Assert.That(lines).Contains($"before jobs: .NET 10.0(Runtime=.NET 10.0, Toolchain=net10.0) | {job}");
        await Assert.That(lines).Contains(
            "warning: Health/GetHealthAsync [Fixture=health] (.NET 10.0) measured a changed fixture: "
            + "wire 49 B / 1 items / 49 B per item before, 98 B / 1 items / 49 B per item after.");
    }

    [Test]
    [Arguments("WireBytes", "98 B / 1 items / 49 B per item")]
    [Arguments("WireItems", "49 B / 98 items / 49 B per item")]
    [Arguments("PayloadBytesPerItem", "49 B / 1 items / 98 B per item")]
    public async Task Execute_Should_Warn_When_A_Matched_Case_Changed_Its_Fixture(string metricId, string afterFigures)
    {
        var before = new FixtureLoader().Load("Benchmarks.wire-metrics-report-full.json");
        var result = await CompareAsync(before, WithMetric(before, metricId, 98));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var warnings = result.Output.Split('\n').Where(line => line.Contains("measured a changed fixture", StringComparison.Ordinal));
        await Assert.That(string.Join('\n', warnings)).IsEqualTo(
            "warning: Health/GetHealthAsync [Fixture=health] (.NET 10.0) measured a changed fixture: "
            + "wire 49 B / 1 items / 49 B per item before, " + afterFigures + " after.");
    }

    [Test]
    [Arguments("Benchmarks.wire-metrics-report-full.json", "Benchmarks.wire-metrics-report-full.json")]
    [Arguments("Benchmarks.wire-metrics-report-full.json", "Benchmarks.net10-only-report-full.json")]
    [Arguments("Benchmarks.net10-only-report-full.json", "Benchmarks.wire-metrics-report-full.json")]
    public async Task Execute_Should_Not_Warn_For_Equal_Or_One_Sided_Wire_Figures(string beforeFixture, string afterFixture)
    {
        var loader = new FixtureLoader();
        var result = await CompareAsync(loader.Load(beforeFixture), loader.Load(afterFixture));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).DoesNotContain("changed fixture");
    }

    [Test]
    public async Task Execute_Should_Warn_When_The_Runs_Carry_Different_Runtime_Legs()
    {
        var fileSystem = new MockFileSystem();
        var loader = new FixtureLoader();
        fileSystem.Initialize().With(
            new FileDescription(BenchmarkRunData.BeforeReportPath, loader.Load(BenchmarkRunData.BeforeFixture)),
            new FileDescription(
                BenchmarkRunData.AfterReportPath,
                loader.Load("Benchmarks.net10-only-report-full.json")));
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(
        [
            "compare-benchmarks",
            BenchmarkRunData.BeforeDirectory,
            BenchmarkRunData.AfterDirectory,
        ]);

        // The net10.0 cases still join, so the comparison succeeds — but the missing net472 leg
        // must be named instead of silently draining into the one-sided lists.
        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("warning:");
        await Assert.That(result.Output).Contains("different runtime legs");
        await Assert.That(result.Output).Contains("before: '.NET 10.0', '.NET Framework 4.7.2'");
        await Assert.That(result.Output).Contains("after: '.NET 10.0'");
    }

    private static string WithMetric(string report, string metricId, int value)
    {
        var document = JsonNode.Parse(report)!; // The fixture contains the named metric on its first case.
        var metric = document["Benchmarks"]![0]!["Metrics"]!.AsArray()
            .Single(metric => metric!["Descriptor"]!["Id"]!.GetValue<string>() == metricId)!;
        metric["Value"] = value;
        return document.ToJsonString();
    }

    private static async Task<CommandAppResult> CompareAsync(string beforeReport, params string[] afterReports)
    {
        using var console = new TestConsole();
        console.Width(400);
        return await CompareAsync(console, beforeReport, afterReports);
    }

    private static async Task<CommandAppResult> CompareAsync(TestConsole console, string beforeReport, params string[] afterReports)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription(BenchmarkRunData.BeforeReportPath, beforeReport));
        for (var index = 0; index < afterReports.Length; index++)
        {
            fileSystem.Initialize().With(new FileDescription($"{BenchmarkRunData.AfterDirectory}/results/{index.ToString(CultureInfo.InvariantCulture)}-report-full.json", afterReports[index]));
        }

        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole>(console);
        });
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings(), console);
        tester.Configure(ToolApp.Configure);
        return await tester.RunAsync(["compare-benchmarks", BenchmarkRunData.BeforeDirectory, BenchmarkRunData.AfterDirectory]);
    }
}
