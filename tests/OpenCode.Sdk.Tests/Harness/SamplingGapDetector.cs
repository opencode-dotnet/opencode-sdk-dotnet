using System.Globalization;
using System.Runtime.ExceptionServices;

namespace OpenCode.Sdk.Tests.Harness;

internal sealed class SamplingGapDetector(TimeProvider time, Func<TimeSpan, bool> wait, SamplingGapDetector.IGcReader gc)
{
    /// <summary>The interruptible sampling interval, in milliseconds.</summary>
    internal const int TickMilliseconds = 250;
    /// <summary>The excess above the tick that a gap must exceed, in seconds.</summary>
    internal const int ThresholdSeconds = 2;
    /// <summary>The maximum number of sampled intervals retained for attribution.</summary>
    internal const int Capacity = 1000;
    /// <summary>The maximum wait for the detector's worker to end, in seconds.</summary>
    internal const int JoinSeconds = 5;

    private readonly Lock _gate = new();
    private readonly Queue<Gap> _gaps = new();
    private Observation? _previous;
    private GcReading? _previousGc;
    private long _count;
    private TimeSpan _longest;
    private Thread? _thread;
    private ExceptionDispatchInfo? _fault;

    internal class DetectorThreadJoin
    {
        public virtual bool Join(Thread thread, TimeSpan bound) => thread.Join(bound);
    }

    public Thread Worker => _thread ?? throw new InvalidOperationException("the detector has not started");

    public void Start()
    {
        Sample();
        _thread = new Thread(SampleUntilStopped)
        {
            IsBackground = true,
            Name = "opencode-tests-sampling-gap",
        };
        _thread.Start();
    }

    public string EndSession(ManualResetEventSlim stop, DetectorThreadJoin join)
    {
        ExceptionDispatchInfo? sampleFault = null;
        try
        {
            Sample();
        }
        catch (Exception exception)
        {
            sampleFault = ExceptionDispatchInfo.Capture(exception);
        }

        stop.Set();
        if (!join.Join(Worker, TimeSpan.FromSeconds(JoinSeconds)))
        {
            throw new InvalidOperationException("the stall detector's thread did not end");
        }

        _fault?.Throw();
        sampleFault?.Throw();
        return Summary;
    }

    private void SampleUntilStopped()
    {
        try
        {
            while (!wait(TimeSpan.FromMilliseconds(TickMilliseconds)))
            {
                Sample();
            }
        }
        catch (Exception exception)
        {
            _ = Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(exception), null);
        }
    }

    internal interface IGcReader
    {
        public GcReading Read();
    }

    internal sealed record GcReading(int Gen0, int Gen1, int Gen2, TimeSpan? Pause);
    internal sealed record Observation(long Timestamp, DateTimeOffset Utc);
    internal sealed record Gap(Observation Start, Observation End, TimeSpan Elapsed, TimeSpan Excess, GcReading Gc)
    {
        public string Format(int pid, string? imageOs, string? imageVersion)
        {
            var image = string.IsNullOrWhiteSpace(imageOs) || string.IsNullOrWhiteSpace(imageVersion)
                ? "n/a" : imageOs + " " + imageVersion;
            var pause = Gc.Pause is { } duration
                ? string.Format(CultureInfo.InvariantCulture, "+{0:F2} s", duration.TotalSeconds) : "n/a (net472)";
            return string.Format(CultureInfo.InvariantCulture,
                "sampling-gap: elapsed {0:F2} s (excess {1:F2} s over the {2} ms tick, threshold {3} s), interval {4:yyyy-MM-ddTHH:mm:ss.ffZ} to {5:HH:mm:ss.ffZ} overlaps this test; gc gen0 +{6} gen1 +{7} gen2 +{8}, pause {9}; host pid {10}; image {11}",
                Elapsed.TotalSeconds, Excess.TotalSeconds, TickMilliseconds, ThresholdSeconds, Start.Utc, End.Utc,
                Gc.Gen0, Gc.Gen1, Gc.Gen2, pause, pid, image);
        }
    }

    public void Report(long start, Action<Gap> write)
    {
        Sample();
        var end = time.GetTimestamp();
        foreach (var gap in Overlapping(start, end))
        {
            write(gap);
        }
    }

    public IReadOnlyList<Gap> Overlapping(long start, long end) =>
        [.. Gaps.Where(gap => gap.Start.Timestamp <= end && gap.End.Timestamp >= start)];

    public string Summary
    {
        get
        {
            lock (_gate)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "sampling-gap summary: count {0}; longest {1:F2} s; retained {2}", _count, _longest.TotalSeconds, _gaps.Count);
            }
        }
    }

    public IReadOnlyList<Gap> Gaps
    {
        get
        {
            lock (_gate)
            {
                return [.. _gaps];
            }
        }
    }

    public void Sample()
    {
        lock (_gate)
        {
            var now = new Observation(time.GetTimestamp(), time.GetUtcNow());
            var reading = gc.Read();
            if (_previous is { } previous && _previousGc is { } previousGc)
            {
                var elapsed = time.GetElapsedTime(previous.Timestamp, now.Timestamp);
                var excess = elapsed - TimeSpan.FromMilliseconds(TickMilliseconds);
                if (excess > TimeSpan.FromSeconds(ThresholdSeconds))
                {
                    _count++;
                    _longest = elapsed > _longest ? elapsed : _longest;
                    _gaps.Enqueue(new Gap(previous, now, elapsed, excess,
                        new GcReading(reading.Gen0 - previousGc.Gen0, reading.Gen1 - previousGc.Gen1,
                            reading.Gen2 - previousGc.Gen2, reading.Pause - previousGc.Pause)));
                    if (_gaps.Count > Capacity)
                    {
                        _ = _gaps.Dequeue();
                    }
                }
            }

            _previous = now;
            _previousGc = reading;
        }
    }
}
