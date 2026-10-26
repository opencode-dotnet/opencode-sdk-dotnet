using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>Prompt request serialization and the complete operation over a canned acknowledgement.</summary>
/// <remarks>
/// Wire figures describe the request body only. The complete row's Alloc/Wire divides by that
/// body only, as in SessionCreateRequestBenchmarks (#626); read request amplification from the
/// serialization row. Setup compares against literal expected bodies in BenchmarkFixtures.
/// </remarks>
[MemoryDiagnoser]
public class SessionPromptRequestBenchmarks : IDisposable
{
    private const string Text = "Review the session transport for cancellation bugs. Explain the failure path and suggest a focused fix. Keep the public API unchanged.";
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private SessionPromptRequest _request = new() { Text = Text };
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private SessionClient? _session;

    public static IEnumerable<WireFixture> Fixtures()
    {
        foreach (var name in new[] { "text-only", "text-files", "all-set" })
        {
            var body = BenchmarkFixtures.SessionPromptRequestBody(name);
            yield return new WireFixture(name, body, items: 1, payloadBytesPerItem: body.Length);
        }
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _request = RequestFor(Fixture.Name);
        if (!SerializeRequest().AsSpan().SequenceEqual(Fixture.Bytes))
        {
            throw new InvalidOperationException($"Prompt case '{Fixture.Name}' did not serialize to its wire body.");
        }

        _handler = new CannedResponseHandler(BenchmarkFixtures.SessionPromptResponseBody());
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _session = _client.Sessions.GetSessionClient("ses_bench0000000000000000001");
        var response = await PromptAsync().ConfigureAwait(false);
        if (response.Prompt is not { Id: "inb_bench", Payload.Text: "Accepted." })
        {
            throw new InvalidOperationException("The prompt acknowledgement did not materialize its inbox item.");
        }
    }

    [Benchmark]
    public byte[] SerializeRequest() => JsonSerializer.SerializeToUtf8Bytes(_request, OpenCodeJsonContext.Default.SessionPromptRequest);

    [Benchmark]
    public Task<SessionPromptResponse> PromptAsync() => _session!.PromptAsync(_request);

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    private static SessionPromptRequest RequestFor(string name)
    {
        var request = new SessionPromptRequest { Text = Text };
        var files = new[]
        {
            new PromptInputFileAttachment { Uri = "file:///repo/src/Transport.cs", Name = "Transport.cs", Description = "HTTP transport" },
            new PromptInputFileAttachment { Uri = "file:///repo/tests/TransportTests.cs", Name = "TransportTests.cs", Description = "Cancellation tests" },
        };
        using var metadata = JsonDocument.Parse("7");
        return name switch
        {
            "text-only" => request,
            "text-files" => request with { Files = files },
            "all-set" => request with
            {
                Id = "msg_bench",
                Files = files,
                Agents = [new PromptAgentAttachment { Name = "build" }],
                Skills = [new PromptInputSkillAttachment { Id = "review" }],
                Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["run"] = metadata.RootElement.Clone() },
                Delivery = SessionInboxDelivery.Queue,
                Resume = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such prompt case."),
        };
    }
}
