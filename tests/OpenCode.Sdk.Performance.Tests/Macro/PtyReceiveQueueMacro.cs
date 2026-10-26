using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Macro;

/// <summary>Measures terminal receive-queue retention without a consumer and while draining a completed burst.</summary>
internal sealed class PtyReceiveQueueMacro
{
    private const int Kilobyte = 1024;
    private const int SmallPayloadSize = 64;
    private const int MediumPayloadSize = 16 * Kilobyte;
    private const int LargePayloadSize = 64 * Kilobyte;
    private const int MaximumAbsentCount = 200_000;
    private const int AbsentPayloadBytes = 64 * Kilobyte * Kilobyte;
    private const int WarmupCount = 100;
    private const int QuickDivisor = 100;
    private const int FullDivisor = 1;
    private const int SmallBurstCount = 100_000;
    private const int MediumBurstCount = 10_000;
    private const int SampleCapacity = 100;
    private const int RowCapacity = 3_000;
    private const int UsageExitCode = 2;
    private const int MacroPrefixLength = 2;

    private readonly List<Row> _rows = new(RowCapacity);
    private readonly List<string> _absent = [];
    private readonly List<string> _burst = [];
    private readonly List<string> _formulas = [];
    private string _tfm = string.Empty;

    public async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is not ["--macro", "pty-receive-queue", ..]
            || args.Skip(MacroPrefixLength).Any(static option => !string.Equals(option, "--quick", StringComparison.Ordinal)))
        {
            await Console.Error.WriteLineAsync("Usage: --macro pty-receive-queue [--quick]").ConfigureAwait(false);
            return UsageExitCode;
        }

        _tfm = typeof(PtyReceiveQueueMacro).Assembly.GetCustomAttributes(false)
            .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>().Single().FrameworkName;
        var divisor = args.Contains("--quick", StringComparer.Ordinal) ? QuickDivisor : FullDivisor;
        foreach (var family in new[] { nameof(PtySession), nameof(PersistentPtySession) })
        {
            foreach (var size in new[] { SmallPayloadSize, MediumPayloadSize, LargePayloadSize })
            {
                var count = Math.Min(MaximumAbsentCount, AbsentPayloadBytes / size) / divisor;
                await MeasureAsync(family, "absent", size, WarmupCount / divisor, record: false).ConfigureAwait(false);
                await MeasureAsync(family, "absent", size, count, record: true).ConfigureAwait(false);
            }

            foreach (var (size, count) in new[] { (SmallPayloadSize, SmallBurstCount), (MediumPayloadSize, MediumBurstCount) })
            {
                await MeasureAsync(family, "burst", size, WarmupCount / divisor, record: false).ConfigureAwait(false);
                await MeasureAsync(family, "burst", size, count / divisor, record: true).ConfigureAwait(false);
            }
        }

        WriteOutput();
        return 0;
    }

    private async Task MeasureAsync(string family, string scenario, int size, int count, bool record)
    {
        var samples = new List<Sample>(SampleCapacity);
        using var memory = new RetainedMemory();
        using var socket = new SyntheticTerminalWebSocket(size,
            string.Equals(family, nameof(PtySession), StringComparison.Ordinal) ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            count, delivered =>
            {
                samples.Add(new Sample(delivered, memory.ReadRetainedBytes(), memory.ReadPrivateBytes()));
            });
        if (string.Equals(family, nameof(PtySession), StringComparison.Ordinal))
        {
            await using var session = new PtySession(socket, TimeProvider.System);
            await MeasureSessionAsync(session, session.ReadAsync()).ConfigureAwait(false);
        }
        else
        {
            await using var core = new TerminalSocketCore<PersistentPtyFrame>(socket, PersistentPtyFrameDecoder.Instance,
                PersistentPtyClosePolicy.Instance, typeof(PersistentPtySession), TimeProvider.System);
            await using var session = new PersistentPtySession(core, new PersistentPtySeed().Attachment());
            await MeasureSessionAsync(session, session.ReadAsync()).ConfigureAwait(false);
        }

        async Task MeasureSessionAsync<TFrame>(IAsyncDisposable session, IAsyncEnumerable<TFrame> frames)
        {
            var baselinePrivate = memory.ReadPrivateBytes();
            var baseline = memory.ReadRetainedBytes();
            socket.OpenGate();
            await socket.WaitForEnqueueBarrierAsync().ConfigureAwait(false);
            var endpoint = memory.ReadRetainedBytes();
            var endpointPrivate = memory.ReadPrivateBytes();
            var perFrame = (endpoint - baseline) / (double)count;
            var peak = memory.PeakRetainedBytes;
            var drain = 0.0;
            if (string.Equals(scenario, "burst", StringComparison.Ordinal))
            {
                var watch = Stopwatch.StartNew();
                await DrainAsync(frames, count, size).ConfigureAwait(false);
                watch.Stop();
                drain = watch.Elapsed.TotalMilliseconds;
            }

            var afterDrain = string.Equals(scenario, "burst", StringComparison.Ordinal) ? memory.ReadRetainedBytes() : 0;
            await session.DisposeAsync().ConfigureAwait(false);
            var afterDispose = memory.ReadRetainedBytes();
            if (record)
            {
                Add("baseline_retained", baseline, "B");
                Add("baseline_private", baselinePrivate, "B");
                RecordSamples(family, scenario, size, samples);

                Add("endpoint_retained", endpoint, "B");
                Add("endpoint_private", endpointPrivate, "B");
                Add("delivered", socket.Delivered, "messages");
                Add("barrier_queued", count, "frames");
                Add("receive_requests", socket.ReceiveRequests, "requests");
                Add("incremental_retained_per_queued_frame", perFrame, "B/frame");
                Add("overhead_factor", perFrame / size, "ratio");
                Add("after_dispose_delta", afterDispose - baseline, "B");
                if (string.Equals(scenario, "absent", StringComparison.Ordinal))
                {
                    _absent.Add(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3:F2} | {4:F3} | {5} |", family, size, count, perFrame, perFrame / size, afterDispose - baseline));
                    _formulas.Add(string.Format(CultureInfo.InvariantCulture, "- {0}, {1} B: retained growth = (P - C) x t x {2:F2} B/frame.", family, size, perFrame));
                }
                else
                {
                    Add("peak_retained", peak, "B");
                    Add("drain_time", drain, "ms");
                    Add("after_drain_delta", afterDrain - baseline, "B");
                    _burst.Add(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3} | {4:F3} | {5} |", family, size, count, peak, drain, afterDrain - baseline));
                }
            }
        }

        void Add(string metric, double value, string unit) =>
            _rows.Add(new Row(family, scenario, size, count, metric, value, unit));
    }

    private void RecordSamples(string family, string scenario, int size, IEnumerable<Sample> samples)
    {
        foreach (var sample in samples)
        {
            _rows.Add(new Row(family, scenario, size, sample.Delivered, "sample_delivered", sample.Delivered, "messages"));
            _rows.Add(new Row(family, scenario, size, sample.Delivered, "sample_retained", sample.Retained, "B"));
            _rows.Add(new Row(family, scenario, size, sample.Delivered, "sample_private", sample.Private, "B"));
        }
    }

    private static async Task DrainAsync<TFrame>(IAsyncEnumerable<TFrame> frames, int count, int size)
    {
        await using var reader = frames.GetAsyncEnumerator();
        var drained = 0;
        while (drained < count && await reader.MoveNextAsync().ConfigureAwait(false))
        {
            var length = reader.Current switch
            {
                PtyOutputFrame output => output.Text.Length,
                PersistentPtyOutputFrame output => output.Data.Length,
                _ => throw new InvalidOperationException("The synthetic message did not decode into an output frame."),
            };
            if (length != size)
            {
                throw new InvalidOperationException("The synthetic output frame has the wrong size.");
            }

            drained++;
        }

        if (drained != count)
        {
            throw new InvalidOperationException("The drained frame count differs from the queued count.");
        }
    }

    private void WriteOutput()
    {
        var output = new StringBuilder();
        output.AppendLine("tfm,family,scenario,size,count,metric,value,unit");
        foreach (var row in _rows)
        {
            output.AppendFormat(CultureInfo.InvariantCulture, "\"{0}\",{1},{2},{3},{4},{5},{6:R},{7}",
                _tfm, row.Family, row.Scenario, row.Size, row.Count, row.Metric, row.Value, row.Unit).AppendLine();
        }

        output.AppendLine();
        output.AppendLine("# Terminal receive queue");
        output.AppendLine("## Environment");
        output.Append("- OS: ").AppendLine(RuntimeInformation.OSDescription);
        output.Append("- CPU: ").AppendLine(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
            ?? string.Format(CultureInfo.InvariantCulture, "{0}, {1} logical cores", RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount));
        output.AppendFormat(CultureInfo.InvariantCulture, "- Logical cores: {0}", Environment.ProcessorCount).AppendLine();
        output.Append("- Runtime: ").Append(RuntimeInformation.FrameworkDescription).Append(" (").Append(Environment.Version).AppendLine(")");
        output.Append("- TFM: ").AppendLine(_tfm);
        output.Append("- Process architecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        output.Append("- GC: ").Append(GCSettings.IsServerGC ? "server" : "workstation")
            .Append("; concurrent: ").Append(GCSettings.LatencyMode is GCLatencyMode.Interactive or GCLatencyMode.SustainedLowLatency)
            .Append("; latency mode: ").AppendLine(GCSettings.LatencyMode.ToString());
        output.AppendLine("- Baseline includes session construction and the receive buffer; incremental retained bytes are (endpoint - baseline) / queued frames.");
        output.AppendLine("- Samples count queued frames at the next receive request. Receives complete on the thread pool, not necessarily with a pump suspension each time.");
        output.AppendLine("## Absent consumer");
        output.AppendLine("| Family | Size (B) | Frames | Incremental B/frame | Overhead / payload | Disposal delta (B) |");
        output.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var line in _absent)
        {
            output.AppendLine(line);
        }

        output.AppendLine();
        output.AppendLine("No slow-consumer run: P is the producer rate and C is the consumer rate in frames/s; t is seconds. Disposal deltas are observations, not a promise of an exact return to baseline.");
        foreach (var line in _formulas)
        {
            output.AppendLine(line);
        }

        output.AppendLine("## Burst");
        output.AppendLine("| Family | Size (B) | Frames | Peak retained (B) | Drain (ms) | After-drain delta (B) |");
        output.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var line in _burst)
        {
            output.AppendLine(line);
        }

        Console.WriteLine(output.ToString());
    }

    /// <summary>One measurement with its scenario and CSV dimensions.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Row(string Family, string Scenario, int Size, int Count, string Metric, double Value, string Unit);

    /// <summary>A memory checkpoint taken after the delivered frames enter the queue.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Sample(int Delivered, long Retained, long Private);
}
