using System.Globalization;

namespace OpenCode.Sdk.Tools.Tests.TestRoster.Support;

/// <summary>Builds the minimal TRX documents the reader consumes: a TestRun whose
/// ResultSummary/Counters report the given totals.</summary>
internal static class TrxContent
{
    /// <summary>The TRX XML namespace of TestRun/ResultSummary/Counters.</summary>
    public const string Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static string WithCounters(int total, int executed)
    {
        var totalText = total.ToString(CultureInfo.InvariantCulture);
        var executedText = executed.ToString(CultureInfo.InvariantCulture);
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="{{Namespace}}">
              <ResultSummary outcome="Completed">
                <Counters total="{{totalText}}" executed="{{executedText}}" passed="{{executedText}}" failed="0" />
              </ResultSummary>
            </TestRun>
            """;
    }
}
