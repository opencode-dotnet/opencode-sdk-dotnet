using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>Replays complete captured turns over the buffered and suspending live event bus.</summary>
[MemoryDiagnoser]
public class RealTurnEventBenchmarks : IDisposable
{
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private readonly OpenCodeClient?[] _clients = new OpenCodeClient?[2];
    private readonly EventBenchmarkSetup?[] _connections = new EventBenchmarkSetup?[2];
    private byte[][] _frames = [];
    private JsonTypeInfo[] _metadata = [];
    private byte[][] _expected = [];

    public static IEnumerable<WireFixture> Fixtures()
    {
        yield return Turn("turn-short", "turn-1-short-question.jsonl");
        yield return Turn("turn-coding", "turn-2-coding-read-write.jsonl");
        yield return Turn("turn-multi", "turn-3-multi-step.jsonl");
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var frames = await EventBenchmarkSetup.DecodeFramesAsync(Fixture.Bytes).ConfigureAwait(false);

        if (frames.Count != Fixture.Items + 1)
        {
            throw new InvalidOperationException("The captured turn did not frame into its expected event count.");
        }

        _frames = [.. frames.Skip(1)];
        _metadata = [.. frames.Select(Metadata)];
        _expected = [.. frames.Select((frame, index) => JsonSerializer.SerializeToUtf8Bytes(
            JsonSerializer.Deserialize(frame, _metadata[index]), _metadata[index]))];
        for (var index = 0; index < frames.Count; index++)
        {
            RequireEvent(JsonSerializer.Deserialize(frames[index], OpenCodeJsonContext.Default.IEvent), index);
        }

        for (var index = 0; index < _clients.Length; index++)
        {
            var connection = new EventBenchmarkSetup(Fixture.Bytes, Options, suspendingChunkBytes: index == 0 ? null : 1460);
            _connections[index] = connection;
            var client = connection.Client;
            _clients[index] = client;
            await RequireConnectionAsync(client).ConfigureAwait(false);
        }

        if (_connections[1]?.Handler.LastBody is not { } body || body.SuspendedReads < body.MinimumReads)
        {
            throw new InvalidOperationException("The suspending subscription's reads did not suspend.");
        }
    }

    [Benchmark]
    public Task<int> SubscribeAsync() => ReadConnectionAsync(_clients[0]!);

    [Benchmark]
    public Task<int> SubscribeSuspendingAsync() => ReadConnectionAsync(_clients[1]!);

    [Benchmark]
    public IEvent? DeserializeFrames()
    {
        IEvent? result = null;
        foreach (var frame in _frames)
        {
            result = JsonSerializer.Deserialize(frame, OpenCodeJsonContext.Default.IEvent);
        }

        return result;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        foreach (var connection in _connections)
        {
            connection?.Dispose(disposing);
        }
    }

    private static WireFixture Turn(string name, string resource)
    {
        var frames = BenchmarkFixtures.JsonLines(resource);
        return new WireFixture(name, BenchmarkFixtures.EventStream(frames), frames.Count - 1,
            payloadBytesPerItem: (int)frames.Skip(1).Average(static frame => frame.Length));
    }

    private async Task<int> ReadConnectionAsync(OpenCodeClient client)
    {
        var count = 0;
        await foreach (var item in client.Events.SubscribeAsync().ConfigureAwait(false))
        {
            _ = item;
            if (++count == Fixture.Items + 1)
            {
                break;
            }
        }

        return count;
    }

    private async Task RequireConnectionAsync(OpenCodeClient client)
    {
        var count = 0;
        await foreach (var item in client.Events.SubscribeAsync().ConfigureAwait(false))
        {
            RequireEvent(item, count);
            if (++count == Fixture.Items + 1)
            {
                return;
            }
        }

        throw new InvalidOperationException("The subscription ended before delivering every captured event.");
    }

    private void RequireEvent(IEvent? item, int index)
    {
        if (item?.GetType() != _metadata[index].Type)
        {
            throw new InvalidOperationException($"Event at index {index.ToString(CultureInfo.InvariantCulture)} did not materialize {_metadata[index].Type.Name}.");
        }

        if (!JsonSerializer.SerializeToUtf8Bytes(item, _metadata[index]).AsSpan().SequenceEqual(_expected[index]))
        {
            throw new InvalidOperationException($"Event at index {index.ToString(CultureInfo.InvariantCulture)} differs from concrete metadata or arrived out of order.");
        }
    }

    private static JsonTypeInfo Metadata(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("type").GetString() switch
        {
            "server.connected" => OpenCodeJsonContext.Default.EventServerConnected,
            "models-dev.refreshed" => OpenCodeJsonContext.Default.ModelsDevRefreshed,
            "integration.updated" => OpenCodeJsonContext.Default.IntegrationUpdated,
            "provider.updated" => OpenCodeJsonContext.Default.ProviderUpdated,
            "model.updated" => OpenCodeJsonContext.Default.ModelUpdated,
            "session.created" => OpenCodeJsonContext.Default.SessionCreated,
            "session.inbox.enqueued" => OpenCodeJsonContext.Default.SessionInboxEnqueued,
            "session.execution.started" => OpenCodeJsonContext.Default.SessionExecutionStarted,
            "session.instructions.updated" => OpenCodeJsonContext.Default.SessionInstructionsUpdated,
            "session.inbox.delivered" => OpenCodeJsonContext.Default.SessionInboxDelivered,
            "session.step.started" => OpenCodeJsonContext.Default.SessionStepStarted,
            "session.reasoning.started" => OpenCodeJsonContext.Default.SessionReasoningStarted,
            "session.reasoning.delta" => OpenCodeJsonContext.Default.SessionReasoningDelta,
            "session.reasoning.ended" => OpenCodeJsonContext.Default.SessionReasoningEnded,
            "session.text.started" => OpenCodeJsonContext.Default.SessionTextStarted,
            "session.text.delta" => OpenCodeJsonContext.Default.SessionTextDelta,
            "session.text.ended" => OpenCodeJsonContext.Default.SessionTextEnded,
            "session.step.streamed" => OpenCodeJsonContext.Default.SessionStepStreamed,
            "session.step.ended" => OpenCodeJsonContext.Default.SessionStepEnded,
            "session.usage.updated" => OpenCodeJsonContext.Default.SessionUsageUpdated,
            "session.execution.succeeded" => OpenCodeJsonContext.Default.SessionExecutionSucceeded,
            "session.tool.input.started" => OpenCodeJsonContext.Default.SessionToolInputStarted,
            "session.tool.input.ended" => OpenCodeJsonContext.Default.SessionToolInputEnded,
            "session.tool.called" => OpenCodeJsonContext.Default.SessionToolCalled,
            "session.tool.success" => OpenCodeJsonContext.Default.SessionToolSuccess,
            "permission.asked" => OpenCodeJsonContext.Default.PermissionAsked,
            "permission.replied" => OpenCodeJsonContext.Default.PermissionReplied,
            _ => throw new InvalidOperationException("The capture contains an unmapped event type."),
        };
    }
}
