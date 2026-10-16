using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class RealClockGuardTests
{
    [Test]
    [Arguments("Stopwatch.StartNew();")]
    [Arguments("Stopwatch<T>.StartNew();")]
    [Arguments("new CancellationTokenSource(timeout);")]
    [Arguments("cts.CancelAfter(timeout);")]
    [Arguments("Task.Delay(timeout);")]
    [Arguments("new System.Threading.CancellationTokenSource(timeout);")]
    [Arguments("System.Threading.Tasks.Task.Delay(timeout, fakeTime);")]
    [Arguments("cts?.CancelAfter(timeout);")]
    public async Task RealClockGuard_Should_Report_A_Hit(string source)
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", source).CorpusAsync();

        await Assert.That(RealClockGuard.Find(corpus).Select(static site => site.ToString()))
            .Contains($"tests/P/A.cs:1: Real clock: {RealClockGuard.Text}");
    }

    [Test]
    [Arguments("using Watch = System.Diagnostics.Stopwatch;")]
    [Arguments("new CancellationTokenSource();")]
    [Arguments("// Stopwatch.StartNew(); new CancellationTokenSource(10); cts.CancelAfter(10); Task.Delay(10);")]
    [Arguments("var s = \"Stopwatch.StartNew(); new CancellationTokenSource(10); cts.CancelAfter(10); Task.Delay(10);\";")]
    [Arguments("var s = $\"{Stopwatch.StartNew()}\";")]
    [Arguments("Thread.Sleep(10); task.WaitAsync(timeout); var t = TimeProvider.System;")]
    public async Task RealClockGuard_Should_Ignore_Nonmatching_Source(string source)
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", source).CorpusAsync();

        await Assert.That(RealClockGuard.Find(corpus)).IsEmpty();
    }

    [Test]
    public async Task RealClockGuard_Should_Report_A_Hit_When_References_Share_A_Line()
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", "Stopwatch watch = new Stopwatch();").CorpusAsync();

        await Assert.That(RealClockGuard.Find(corpus).Select(static site => site.Line)).IsEquivalentTo([1, 1], CollectionOrdering.Matching);
    }
}
