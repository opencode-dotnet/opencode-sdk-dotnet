using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// Tagged-union dispatch in isolation, through the same source-generated converters the SDK
/// uses. The interface rows pay the discriminator scan plus nested dispatch (message →
/// assistant content → tool state → tool content); the concrete rows materialize the same bytes
/// into the known record directly and are the no-top-level-dispatch control. Marker position,
/// a duplicated marker, an unknown marker, and message size each vary one thing against the seed.
/// </summary>
[MemoryDiagnoser]
public class UnionDeserializationBenchmarks
{
    private const int MediumParts = 120;
    private const int LargeParts = 2400;
    private const int LargeTitleCharacters = 2048;

    private static readonly Lazy<AssistantMessageComposer> Composer = new(static () =>
        new AssistantMessageComposer(BenchmarkFixtures.DeepAssistantMessage()));

    public static IEnumerable<WireFixture> MessageFixtures()
    {
        var composer = Composer.Value;
        yield return Message("deep-marker-early", composer.MarkerEarly());
        yield return Message("deep-marker-last", composer.MarkerLast());
        yield return Message("deep-duplicate-marker", composer.DuplicateMarkerLastKnown());
        yield return Message("deep-unknown-marker", composer.UnknownMarker());
        yield return Message("medium-message", composer.WithContentParts(MediumParts));
        yield return Message("large-message", composer.WithContentParts(LargeParts));
    }

    public static IEnumerable<WireFixture> ConcreteMessageFixtures()
    {
        var composer = Composer.Value;
        yield return Message("deep-marker-early", composer.MarkerEarly());
        yield return Message("medium-message", composer.WithContentParts(MediumParts));
        yield return Message("large-message", composer.WithContentParts(LargeParts));
    }

    public static IEnumerable<WireFixture> LogItemFixtures()
    {
        yield return Message("log-synced", BenchmarkFixtures.SessionLogSyncedBody());
        yield return Message("session-deleted", BenchmarkFixtures.SessionDeletedBody());
        yield return Message("session-created-2048", BenchmarkFixtures.LargeSessionCreatedBody(LargeTitleCharacters));
    }

    public static IEnumerable<WireFixture> LogSyncedFixtures()
    {
        yield return Message("log-synced", BenchmarkFixtures.SessionLogSyncedBody());
    }

    [GlobalSetup]
    public void Setup()
    {
        foreach (var fixture in MessageFixtures())
        {
            var expectUnknown = string.Equals(fixture.Name, "deep-unknown-marker", StringComparison.Ordinal);
            var materialized = DeserializeMessage(fixture);
            if (expectUnknown ? materialized is not UnknownSessionMessageInfo : materialized is not SessionMessageAssistant)
            {
                throw new InvalidOperationException($"Fixture '{fixture.Name}' did not dispatch to the expected variant.");
            }
        }

        if (LogItemFixtures().FirstOrDefault(fixture => DeserializeLogItem(fixture) is null or UnknownSessionLogItem) is { } undispatched)
        {
            throw new InvalidOperationException($"Fixture '{undispatched.Name}' did not dispatch to a known log item.");
        }
    }

    /// <summary>Interface dispatch: discriminator scan, then materialization through the selected variant.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(MessageFixtures))]
    public ISessionMessageInfo? DeserializeMessage(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.ISessionMessageInfo);
    }

    /// <summary>The control: the same bytes materialized into the known record without top-level dispatch.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(ConcreteMessageFixtures))]
    public SessionMessageAssistant? DeserializeConcreteMessage(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.SessionMessageAssistant);
    }

    /// <summary>Shallow interface dispatch over the small durable items a session log carries most of.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(LogItemFixtures))]
    public ISessionLogItem? DeserializeLogItem(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.ISessionLogItem);
    }

    /// <summary>
    /// The shallow control: the watermark materialized into its record without dispatch. It returns
    /// <see cref="object"/> because BenchmarkDotNet's generated runner names a benchmark's return
    /// type and does not inherit this project's experimental-diagnostic arbitration, so naming the
    /// experimental record there would fail that build.
    /// </summary>
    [Benchmark]
    [ArgumentsSource(nameof(LogSyncedFixtures))]
    public object? DeserializeConcreteLogSynced(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.EventLogSynced);
    }

    private static WireFixture Message(string name, byte[] payload) => new(name, payload, items: 1, payloadBytesPerItem: payload.Length);
}
