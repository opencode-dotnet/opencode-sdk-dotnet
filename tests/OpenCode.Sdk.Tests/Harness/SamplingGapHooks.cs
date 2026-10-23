using OpenCode.Sdk.Tests.Support;
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
    public static void EndSession()
    {
        try
        {
            Console.WriteLine(Detector.EndSession(Stop, new DetectorThreadJoin()));
        }
        finally
        {
            if (!Detector.Worker.IsAlive)
            {
                Stop.Dispose();
            }
        }
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
