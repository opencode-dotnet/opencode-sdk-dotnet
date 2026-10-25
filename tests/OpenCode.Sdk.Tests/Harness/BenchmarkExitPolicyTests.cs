namespace OpenCode.Sdk.Tests.Harness;

public sealed class BenchmarkExitPolicyTests
{
    [Test]
    [Arguments(true, false, 0, 0, 0)]
    [Arguments(false, true, 0, 0, 0)]
    [Arguments(false, false, 1, 0, 0)]
    [Arguments(false, false, 0, 1, 0)]
    [Arguments(false, false, 0, 0, 1)]
    public async Task GetExitCode_Should_Reject_Any_Failed_Summary(
        bool hasValidationErrors, bool hasBuildFailures, int missingReports, int unsuccessfulReports, int missingStatistics)
    {
        var failure = new BenchmarkExitPolicy.SummaryResult(
            hasValidationErrors, hasBuildFailures, missingReports, unsuccessfulReports, missingStatistics);

        var code = BenchmarkExitPolicy.GetExitCode([new(false, false, 0, 0, 0), failure]);

        await Assert.That(code).IsEqualTo(1);
    }

    [Test]
    public async Task GetExitCode_Should_Accept_All_Clear_Summaries()
    {
        var code = BenchmarkExitPolicy.GetExitCode([new(false, false, 0, 0, 0), new(false, false, 0, 0, 0)]);

        await Assert.That(code).IsEqualTo(0);
    }
}
