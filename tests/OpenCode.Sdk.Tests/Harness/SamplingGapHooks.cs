using System.Globalization;
using System.Runtime.InteropServices;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;
using static OpenCode.Sdk.Tests.Harness.SamplingGapDetector;

namespace OpenCode.Sdk.Tests.Harness;

internal static class SamplingGapHooks
{
    private const string WindowStart = "sampling-gap-window-start";
    private static readonly ManualResetEventSlim Stop = new();
    private static readonly SamplingGapDetector Detector = new(TimeProvider.System, WaitTick, new SystemGcReader());
    private static readonly int ProcessId = HostDescriptors.CurrentProcessId();
    private static readonly string? ImageOs = Environment.GetEnvironmentVariable("ImageOS");
    private static readonly string? ImageVersion = Environment.GetEnvironmentVariable("ImageVersion");

    [Before(TestSession)]
    public static void Start() => Detector.Start();

    [BeforeEvery(Test)]
    public static void OpenWindow(TestContext context) => context.StateBag[WindowStart] = TimeProvider.System.GetTimestamp();

    [AfterEvery(Test)]
    public static void Report(TestContext context)
    {
        // A test that failed before its BeforeEvery hook ran has no window to attribute a gap to.
        if (!context.StateBag.TryGetValue<long>(WindowStart, out var start))
        {
            return;
        }

        Detector.Report(start, gap => context.Output.WriteLine(gap.Format(ProcessId, ImageOs, ImageVersion)));
    }

    [After(TestSession)]
    public static async Task EndSession()
    {
        try
        {
            var summary = Detector.EndSession(Stop, new DetectorThreadJoin());
            Console.WriteLine(summary);
            await WriteSummaryAsync(summary);
        }
        finally
        {
            if (!Detector.IsRunning)
            {
                Stop.Dispose();
            }
        }
    }

    /// <summary>
    /// `dotnet test` does not show a session hook's console output, so the summary also goes to the
    /// results directory, which CI uploads with the run's other test results.
    /// </summary>
    private static async Task WriteSummaryAsync(string summary)
    {
        var fileSystem = new RealFileSystem();
        var directory = new TestResultsDirectory(fileSystem).Resolve(Environment.GetCommandLineArgs());
        _ = fileSystem.Directory.CreateDirectory(directory);
        var path = fileSystem.Path.Combine(directory, "sampling-gap-summary-" + ProcessId.ToString(CultureInfo.InvariantCulture) + ".txt");
        var text = summary + " (" + RuntimeInformation.FrameworkDescription + ")" + Environment.NewLine;
#if NET
        await fileSystem.File.WriteAllTextAsync(path, text);
#else
        await Task.Run(() => fileSystem.File.WriteAllText(path, text));
#endif
    }

    private static bool WaitTick(TimeSpan tick)
    {
        // A sampler's tick is not a sleep used to synchronize a test; it is an interruptible wait on the stop event, so stop returns at once.
        return Stop.Wait(tick);
    }

    private sealed class SystemGcReader : IGcReader
    {
        public GcReading Read() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
#if NET
            GC.GetTotalPauseDuration());
#else
            null);
#endif
    }
}
