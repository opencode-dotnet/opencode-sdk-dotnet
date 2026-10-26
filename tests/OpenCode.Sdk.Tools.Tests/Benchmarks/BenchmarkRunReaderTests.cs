using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Sdk.Tools.Benchmarks;
using OpenCode.Sdk.Tools.Benchmarks.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Benchmarks;

public sealed class BenchmarkRunReaderTests
{
    private readonly FixtureLoader _fixtures = new();

    [Test]
    public async Task ReadAsync_Should_Project_Every_Case_From_The_Results_Folder()
    {
        var reader = new BenchmarkRunReader(BenchmarkRunData.CreateComparisonStore());

        var cases = await reader.ReadAsync(BenchmarkRunData.BeforeDirectory, CancellationToken.None);

        await Assert.That(cases.Count).IsEqualTo(4);
        var healthCase = cases.Single(runCase =>
            runCase.Method == "GetHealthAsync" && runCase.Runtime == ".NET 10.0");
        await Assert.That(healthCase.Family).IsEqualTo("Health");
        await Assert.That(healthCase.Parameters).IsEqualTo("Fixture=health");
        await Assert.That(healthCase.FullName)
            .IsEqualTo("OpenCode.Sdk.Performance.Tests.Benchmarks.HealthBenchmarks.GetHealthAsync(Fixture: health)");
        await Assert.That(healthCase.AllocatedBytes).IsEqualTo(2104L);
        await Assert.That(healthCase.MedianNanoseconds).IsEqualTo(1351.6587257385254);
        await Assert.That(healthCase.CaseLabel).IsEqualTo("Health/GetHealthAsync [Fixture=health]");
    }

    [Test]
    public async Task ReadAsync_Should_Read_Reports_From_The_Directory_Itself_When_No_Results_Folder_Exists()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription(
            "runs/flat-report-full.json",
            _fixtures.Load(BenchmarkRunData.BeforeFixture)));
        var reader = new BenchmarkRunReader(fileSystem);

        var cases = await reader.ReadAsync("runs", CancellationToken.None);

        await Assert.That(cases.Count).IsEqualTo(4);
    }

    [Test]
    public async Task ReadAsync_Should_Fall_Back_To_The_Job_Name_When_The_Display_Info_Carries_No_Runtime()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription(
            "runs/results/single-report-full.json",
            _fixtures.Load("Benchmarks.single-job-report-full.json")));
        var reader = new BenchmarkRunReader(fileSystem);

        var cases = await reader.ReadAsync("runs", CancellationToken.None);

        await Assert.That(cases.Single().Runtime).IsEqualTo("DefaultJob");
    }

    [Test]
    public async Task ReadAsync_Should_Populate_The_Wire_Metrics_When_The_Report_Carries_Them()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription(
            "runs/results/wire-metrics-report-full.json",
            _fixtures.Load("Benchmarks.wire-metrics-report-full.json")));
        var reader = new BenchmarkRunReader(fileSystem);

        var cases = await reader.ReadAsync("runs", CancellationToken.None);

        var wireCase = cases.Single(runCase => runCase.Method == "GetHealthAsync");
        await Assert.That(wireCase.WireBytes).IsEqualTo(49L);
        await Assert.That(wireCase.WireItems).IsEqualTo(1L);
        await Assert.That(wireCase.PayloadBytesPerItem).IsEqualTo(49L);
        await Assert.That(wireCase.AllocatedBytes).IsEqualTo(2392L);

        // Wire stays optional per case: the composition rung of the same report carries none.
        var compositionCase = cases.Single(runCase => runCase.Method == "ComposeRequest");
        await Assert.That(compositionCase.HasWireMetrics).IsFalse();
        await Assert.That(compositionCase.AllocatedBytes).IsEqualTo(184L);
    }

    [Test]
    public async Task ReadAsync_Should_Read_An_Archived_Report_Without_Wire_Metrics_With_Wire_Unset()
    {
        var reader = new BenchmarkRunReader(BenchmarkRunData.CreateComparisonStore());

        var cases = await reader.ReadAsync(BenchmarkRunData.BeforeDirectory, CancellationToken.None);

        await Assert.That(cases.Count).IsEqualTo(4);
        foreach (var runCase in cases)
        {
            await Assert.That(runCase.WireBytes).IsNull();
            await Assert.That(runCase.WireItems).IsNull();
            await Assert.That(runCase.PayloadBytesPerItem).IsNull();
            await Assert.That(runCase.HasWireMetrics).IsFalse();
        }
    }

    [Test]
    public async Task ReadAsync_Should_Read_A_Case_Without_A_Positive_Median_As_Carrying_No_Timing()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription(
            "runs/results/zero-median-report-full.json",
            _fixtures.Load("Benchmarks.zero-median-report-full.json")));
        var reader = new BenchmarkRunReader(fileSystem);

        var cases = await reader.ReadAsync("runs", CancellationToken.None);

        var timedCase = cases.Single(runCase => runCase.Runtime == ".NET 10.0");
        var noiseFloorCase = cases.Single(runCase => runCase.Runtime == ".NET Framework 4.7.2");
        await Assert.That(timedCase.MedianNanoseconds).IsEqualTo(0.08522830903530121);
        await Assert.That(noiseFloorCase.MedianNanoseconds).IsNull();
        await Assert.That(noiseFloorCase.AllocatedBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Export_Without_Cases()
    {
        var fileSystem = new MockFileSystem();
        var reportPath = fileSystem.Path.Combine("runs", "results", "empty-report-full.json");
        fileSystem.Initialize().With(new FileDescription(reportPath,
            JsonSerializer.Serialize(new BenchmarkReportDocument { Benchmarks = [] })));
        var message = await RefusalAsync(fileSystem, "runs");

        await Assert.That(message)
            .IsEqualTo($"Benchmark report '{reportPath}' declares no benchmark cases.");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Missing_Run_Directory()
    {
        var message = await RefusalAsync(new MockFileSystem(), "missing");

        await Assert.That(message).Contains("does not exist");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Run_Directory_Without_Full_Json_Exports()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory("runs");
        var message = await RefusalAsync(fileSystem, "runs");

        await Assert.That(message).Contains("*-report-full.json");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Case_Without_Memory_Diagnostics()
    {
        var fileSystem = new MockFileSystem();
        var reportPath = fileSystem.Path.Combine("runs", "results", "missing-memory-report-full.json");
        fileSystem.Initialize().With(new FileDescription(
            reportPath,
            _fixtures.Load("Benchmarks.missing-memory-report-full.json")));
        var message = await RefusalAsync(fileSystem, "runs");

        await Assert.That(message).IsEqualTo(
            "Benchmark case 'OpenCode.Sdk.Performance.Tests.Benchmarks.HealthBenchmarks.GetHealthAsync(Fixture: health)' "
            + $"in '{reportPath}' carries no memory diagnostics; run with MemoryDiagnoser enabled.");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Case_Duplicated_Across_Reports()
    {
        var report = _fixtures.Load(BenchmarkRunData.BeforeFixture);
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(
            new FileDescription("runs/results/first-report-full.json", report),
            new FileDescription("runs/results/second-report-full.json", report));
        var message = await RefusalAsync(fileSystem, "runs");

        await Assert.That(message).Contains("more than once");
    }

    [Test]
    public async Task ReadAsync_Should_Keep_Na_Cases_Without_Measurements()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription("runs/na-report-full.json",
            _fixtures.Load("Benchmarks.na-report-full.json")));

        var cases = await new BenchmarkRunReader(fileSystem).ReadAsync("runs", CancellationToken.None);

        await Assert.That(cases.Select(runCase => runCase.CaseLabel).ToArray()).IsEquivalentTo(
            ["Info/GetInfoAsync [Fixture=info]", "Info/ExecuteWithoutAdapterAsync [Fixture=info]", "Info/AdaptSuccess [Fixture=info]"],
            StringComparer.Ordinal, CollectionOrdering.Matching);
        foreach (var runCase in cases)
        {
            await Assert.That(runCase.Runtime).IsEqualTo("Dry");
            await Assert.That(runCase.IsNa).IsTrue();
            await Assert.That(runCase.AllocatedBytes).IsNull();
            await Assert.That(runCase.MedianNanoseconds).IsNull();
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments(-1L)]
    public async Task ReadAsync_Should_Refuse_Invalid_Memory_With_Statistics(long? allocatedBytes)
    {
        var report = JsonSerializer.Deserialize<BenchmarkReportDocument>(_fixtures.Load(BenchmarkRunData.BeforeFixture));
        Assert.NotNull(report);
        Assert.NotNull(report.Benchmarks);
        var reportCase = report.Benchmarks[0] with { Memory = new BenchmarkReportMemory { BytesAllocatedPerOperation = allocatedBytes } };
        var fileSystem = new MockFileSystem();
        var path = fileSystem.Path.Combine("runs", "invalid-report-full.json");
        fileSystem.Initialize().With(new FileDescription(path,
            JsonSerializer.Serialize(report with { Benchmarks = [reportCase] })));

        var message = await RefusalAsync(fileSystem, "runs");

        await Assert.That(message).IsEqualTo(
            $"Benchmark case '{reportCase.FullName}' in '{path}' carries no memory diagnostics; run with MemoryDiagnoser enabled.");
    }

    private static async Task<string> RefusalAsync(IFileSystem fileSystem, string runDirectory)
    {
        var reader = new BenchmarkRunReader(fileSystem);
        var exception = await Assert.That(async () => _ = await reader.ReadAsync(runDirectory, CancellationToken.None))
            .Throws<BenchmarkRunException>();
        return exception!.Message;
    }
}
