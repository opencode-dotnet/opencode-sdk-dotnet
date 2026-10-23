using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tests.Harness.SamplingGapDetector;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class SamplingGapDetectorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 17, 20, 10, 4, TimeSpan.Zero);

    [Test]
    [Arguments(2249, 0)]
    [Arguments(2250, 0)]
    [Arguments(2251, 1)]
    public async Task Sample_Should_Respect_The_Boundary(int milliseconds, int count)
    {
        var clock = new IndependentTimeProvider();
        var gc = new ScriptedGc();
        var detector = new SamplingGapDetector(clock, _ => true, gc);
        detector.Sample();
        clock.Advance(TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond));
        gc.Reading = new GcReading(2, 3, 4, TimeSpan.FromTicks(400000));
        detector.Sample();
        var gaps = detector.Gaps;
        await Assert.That(gaps.Count).IsEqualTo(count);
        if (count == 1)
        {
            var gap = gaps[0];
            await Assert.That(gap.Elapsed).IsEqualTo(TimeSpan.FromTicks(22510000));
            await Assert.That(gap.Excess).IsEqualTo(TimeSpan.FromTicks(20010000));
            await Assert.That(gap.Start.Timestamp).IsEqualTo(Epoch.UtcTicks);
            await Assert.That(gap.End.Timestamp).IsEqualTo(Epoch.UtcTicks + 22510000);
            await Assert.That(gap.Start.Utc).IsEqualTo(Epoch);
            await Assert.That(gap.End.Utc).IsEqualTo(Epoch.AddTicks(22510000));
            await Assert.That(gap.Gc).IsEqualTo(new GcReading(2, 3, 4, PauseDelta));
        }
    }

    [Test]
    public async Task Sample_Should_Use_Monotonic_Not_Utc_Time()
    {
        using var scenario = new Scenario();
        scenario.Clock.Timestamp += 30000000;
        scenario.Clock.Utc = Epoch.AddTicks(-100000000);
        scenario.Detector.Sample();
        await Assert.That(scenario.Detector.Gaps.Count).IsEqualTo(1);
        await Assert.That(scenario.Detector.Gaps[0].Elapsed).IsEqualTo(TimeSpan.FromTicks(30000000));
        scenario.Clock.Utc = Epoch;
        scenario.Detector.Sample();
        await Assert.That(scenario.Detector.Gaps.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Sample_Should_Not_Duplicate_A_Gap()
    {
        using var scenario = new Scenario();
        scenario.Clock.Advance(TimeSpan.FromTicks(50000000));
        scenario.Detector.Sample();
        scenario.Detector.Sample();
        await Assert.That(scenario.Detector.Gaps.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Report_Should_Sample_The_Pending_Gap_Before_Reading()
    {
        using var scenario = new Scenario();
        scenario.Clock.Advance(TimeSpan.FromTicks(50000000));
        var lines = scenario.Report(Epoch.UtcTicks);
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).StartsWith("sampling-gap: elapsed 5.00 s (excess 4.75 s");
    }

    [Test]
    [Arguments(9, 10, 0)]
    [Arguments(0, 2, 0)]
    [Arguments(5, 10, 1)]
    [Arguments(0, 5, 1)]
    public async Task Overlapping_Should_Check_All_Four_Edges(int start, int end, int count)
    {
        using var scenario = new Scenario();
        scenario.Clock.Advance(TimeSpan.FromTicks(20000000));
        scenario.Detector.Sample();
        scenario.Clock.Advance(TimeSpan.FromTicks(10000000));
        scenario.Detector.Sample();
        scenario.Clock.Advance(TimeSpan.FromTicks(50000000));
        scenario.Detector.Sample();
        var gaps = scenario.Detector.Overlapping(Epoch.UtcTicks + (start * 10000000L), Epoch.UtcTicks + (end * 10000000L));
        await Assert.That(gaps.Count).IsEqualTo(count);
    }

    [Test]
    public async Task Report_Should_Attribute_The_Gap_To_Both_Concurrent_Windows()
    {
        using var scenario = new Scenario();
        scenario.Clock.Advance(TimeSpan.FromTicks(10000000));
        var secondStart = scenario.Clock.GetTimestamp();
        scenario.Clock.Advance(TimeSpan.FromTicks(40000000));
        var first = scenario.Report(Epoch.UtcTicks);
        var second = scenario.Report(secondStart);
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(second).IsEquivalentTo(first, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Report_Should_Print_Gc_Deltas_From_The_Previous_Sample()
    {
        using var scenario = new Scenario(new GcReading(0, 0, 5, TimeSpan.Zero));
        scenario.Gc.Reading = new GcReading(1, 2, 6, TimeSpan.FromTicks(100000));
        scenario.Clock.Advance(TimeSpan.FromTicks(2500000));
        scenario.Detector.Sample();
        scenario.Gc.Reading = new GcReading(3, 5, 8, TimeSpan.FromTicks(500000));
        scenario.Clock.Advance(TimeSpan.FromTicks(30000000));
        var lines = scenario.Report(Epoch.UtcTicks);
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("gc gen0 +2 gen1 +3 gen2 +2, pause " + PauseText);
    }

    [Test]
    public async Task Sample_Should_Evict_The_Oldest_At_Capacity()
    {
        using var scenario = new Scenario();
        scenario.Fill();
        var gaps = scenario.Detector.Gaps;
        await Assert.That(gaps.Count).IsEqualTo(1000);
        await Assert.That(gaps[0].Start.Timestamp).IsEqualTo(Epoch.UtcTicks + 90000000);
        await Assert.That(gaps[^1].End.Timestamp).IsEqualTo(Epoch.UtcTicks + 30090000000);
    }

    [Test]
    public async Task Summary_Should_Keep_Lifetime_Count_And_Longest_After_Eviction()
    {
        using var scenario = new Scenario();
        scenario.Fill();
        await Assert.That(scenario.Detector.Summary).IsEqualTo("sampling-gap summary: count 1001; longest 9.00 s; retained 1000");
    }

    [Test]
    public async Task Report_Should_Leave_The_Test_Outcome_Unchanged()
    {
        using var scenario = new Scenario();
        scenario.Clock.Advance(TimeSpan.FromTicks(50000000));
        var lines = new List<string>();
        await Assert.That(() => scenario.Detector.Report(Epoch.UtcTicks, gap => lines.Add(gap.Format(1234, null, null))))
            .ThrowsNothing();
        await Assert.That(lines.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("win25", "20261021.278.1", "win25 20261021.278.1")]
    [Arguments(null, "20261021.278.1", "n/a")]
    [Arguments("win25", null, "n/a")]
    [Arguments("", "20261021.278.1", "n/a")]
    [Arguments("win25", "", "n/a")]
    [Arguments(" ", "20261021.278.1", "n/a")]
    [Arguments("win25", " ", "n/a")]
    public async Task Format_Should_Match_The_Line_And_Runner_Image(string? os, string? version, string image)
    {
        using var scenario = new Scenario();
        scenario.Clock.Utc = Epoch.AddTicks(4400000);
        scenario.Detector.Sample();
        scenario.Gc.Reading = new GcReading(0, 0, 0, TimeSpan.FromTicks(400000));
        scenario.Clock.Advance(TimeSpan.FromTicks(173300000));
        scenario.Detector.Sample();
        await Assert.That(scenario.Detector.Gaps[0].Format(1234, os, version)).IsEqualTo(
            "sampling-gap: elapsed 17.33 s (excess 17.08 s over the 250 ms tick, threshold 2 s), interval 2026-10-17T20:10:04.44Z to 20:10:21.77Z overlaps this test; gc gen0 +0 gen1 +0 gen2 +0, pause "
            + PauseText + "; host pid 1234; image " + image);
    }

    [Test]
    public async Task EndSession_Should_Join_And_Release_Its_Own_Thread()
    {
        using var scenario = new Scenario();
        scenario.Wait = _ =>
        {
            scenario.Stop.Wait();
            return true;
        };
        scenario.Start();
        await Assert.That(scenario.Detector.Worker.IsBackground).IsTrue();
        await Assert.That(scenario.Detector.Worker.Name).IsEqualTo("opencode-tests-sampling-gap");
        var join = new RecordingJoin();
        _ = scenario.Detector.EndSession(scenario.Stop, join);
        await Assert.That(join.Calls).IsEqualTo(1);
        await Assert.That(join.Thread).IsSameReferenceAs(scenario.Detector.Worker);
        await Assert.That(join.Bound).IsEqualTo(TimeSpan.FromTicks(50000000));
        await Assert.That(scenario.Detector.Worker.IsAlive).IsFalse();
    }

    /// <summary>The final sample comes after the bounded join, so a worker stuck holding the lock cannot keep the join from being reached.</summary>
    [Test]
    public async Task EndSession_Should_Join_Before_Its_Final_Sample()
    {
        using var scenario = new Scenario();
        scenario.Wait = _ =>
        {
            scenario.Stop.Wait();
            return true;
        };
        scenario.Start();
        var readsBefore = scenario.Gc.Reads;
        var readsAtJoin = -1;
        var join = new RecordingJoin { OnJoin = () => readsAtJoin = scenario.Gc.Reads };
        _ = scenario.Detector.EndSession(scenario.Stop, join);
        await Assert.That(readsAtJoin).IsEqualTo(readsBefore);
        await Assert.That(scenario.Gc.Reads).IsEqualTo(readsBefore + 1);
    }

    [Test]
    public async Task EndSession_Should_Fail_When_The_Join_Times_Out()
    {
        using var scenario = new Scenario();
        scenario.Start();
        var error = await Assert.That(() => scenario.Detector.EndSession(scenario.Stop, new TimedOutJoin()))
            .Throws<InvalidOperationException>();
        await Assert.That(error?.Message).IsEqualTo("the stall detector's thread did not end");
        scenario.DrainWorker();
    }

    [Test]
    public async Task EndSession_Should_Rethrow_The_Worker_Fault_With_Its_Stack()
    {
        using var scenario = new Scenario();
        var failure = new InvalidOperationException("scripted GC failure");
        scenario.Wait = _ =>
        {
            scenario.Gc.Failure = failure;
            return false;
        };
        scenario.Start();
        scenario.DrainWorker();
        var error = await Assert.That(() => scenario.Detector.EndSession(scenario.Stop, new RecordingJoin()))
            .Throws<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(error?.StackTrace).Contains(nameof(ScriptedGc) + ".Read");
    }

    private sealed class TimedOutJoin : DetectorThreadJoin
    {
        public override bool Join(Thread thread, TimeSpan bound) => false;
    }

    private sealed class RecordingJoin : DetectorThreadJoin
    {
        public int Calls { get; private set; }
        public Thread? Thread { get; private set; }
        public TimeSpan Bound { get; private set; }
        public Action? OnJoin { get; init; }

        public override bool Join(Thread thread, TimeSpan bound)
        {
            OnJoin?.Invoke();
            Calls++;
            Thread = thread;
            Bound = bound;
            return thread.Join(bound);
        }
    }

#if NET
    private static TimeSpan? PauseDelta => TimeSpan.FromTicks(400000);
    private const string PauseText = "+0.04 s";
#else
    private static TimeSpan? PauseDelta => null;
    private const string PauseText = "n/a (net472)";
#endif

    private sealed class IndependentTimeProvider : TimeProvider
    {
        public long Timestamp { get; set; } = Epoch.UtcTicks;
        public DateTimeOffset Utc { get; set; } = Epoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;

        public void Advance(TimeSpan delta)
        {
            Timestamp += delta.Ticks;
            Utc += delta;
        }
    }

    private sealed class Scenario : IDisposable
    {
        public IndependentTimeProvider Clock { get; } = new();
        public ScriptedGc Gc { get; } = new();
        public ManualResetEventSlim Stop { get; } = new();
        public SamplingGapDetector Detector { get; }
        public Func<TimeSpan, bool> Wait { get; set; } = _ => true;
        private bool _started;

        public Scenario(GcReading? initial = null)
        {
            Gc.Reading = initial ?? new GcReading(0, 0, 0, TimeSpan.Zero);
            Detector = new SamplingGapDetector(Clock, tick => Wait(tick), Gc);
            Detector.Sample();
        }

        public List<string> Report(long start)
        {
            var lines = new List<string>();
            Detector.Report(start, gap => lines.Add(gap.Format(1234, null, null)));
            return lines;
        }

        public void Fill()
        {
            Clock.Advance(TimeSpan.FromTicks(90000000));
            Detector.Sample();
            for (var i = 0; i < 1000; i++)
            {
                Clock.Advance(TimeSpan.FromTicks(30000000));
                Detector.Sample();
            }
        }

        public void Start()
        {
            Detector.Start();
            _started = true;
        }

        public void Dispose()
        {
            Stop.Set();
            if (_started)
            {
                DrainWorker();
            }

            Stop.Dispose();
        }

        public void DrainWorker()
        {
            if (!Detector.Worker.Join(HangGuard.Bound))
            {
                throw new InvalidOperationException("sampling proof worker did not end");
            }
        }
    }

    private sealed class ScriptedGc : IGcReader
    {
        public GcReading Reading { get; set; } = new(0, 0, 0, TimeSpan.Zero);
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }

        public GcReading Read()
        {
            Reads++;
            if (Failure is { } failure)
            {
                Failure = null;
                throw failure;
            }

            return Reading with
            {
#if !NET
                Pause = null,
#endif
            };
        }
    }
}
