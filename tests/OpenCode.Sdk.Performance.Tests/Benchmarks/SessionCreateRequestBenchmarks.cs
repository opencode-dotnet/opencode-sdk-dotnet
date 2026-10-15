using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The request side of session creation, whose eight members are all optional, in each member's
/// three wire states: every member omitted, every member an explicit null, and every member set.
/// The serialization row writes the request body alone; the complete row sends it through the
/// generated operation over a canned created-session answer. Each case's wire figures describe the
/// request body it sends.
/// </summary>
/// <remarks>
/// The expected bodies are written out here as the protocol shapes them, not taken from the
/// serializer, and each request must serialize to its body byte for byte before anything is
/// measured.
/// </remarks>
[MemoryDiagnoser]
public class SessionCreateRequestBenchmarks : IDisposable
{
    private const string Absent = "absent";
    private const string Cleared = "cleared";
    private const string Set = "set";

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private SessionCreateRequest _request = new();
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;

    public static IEnumerable<WireFixture> Fixtures()
    {
        yield return Body(Absent, "{}");
        yield return Body(Cleared,
            "{\"id\":null,\"parentID\":null,\"title\":null,\"agent\":null,\"model\":null,\"location\":null,\"metadata\":null,\"permissions\":null}");
        yield return Body(Set,
            "{\"id\":\"ses_1\",\"parentID\":\"ses_0\",\"title\":\"Fix the build\",\"agent\":\"build\","
            + "\"model\":{\"id\":\"sonnet\",\"providerID\":\"anthropic\"},\"location\":{\"directory\":\"/repo\"},"
            + "\"metadata\":{\"run\":7},\"permissions\":[{\"action\":\"edit\",\"resource\":\"*\",\"effect\":\"ask\"}]}");
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _request = RequestFor(Fixture.Name);
        if (!SerializeRequest().AsSpan().SequenceEqual(Fixture.Bytes))
        {
            throw new InvalidOperationException($"Request state '{Fixture.Name}' did not serialize to its wire body.");
        }

        var created = BenchmarkFixtures.SessionInfoBody();
        _handler = new CannedResponseHandler(BenchmarkFixtures.DataEnvelope(created));
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        var response = await CreateSessionAsync().ConfigureAwait(false);
        if (response.Session is not { Id: "ses_bench0000000000000000001" })
        {
            throw new InvalidOperationException("The created-session answer did not materialize the session.");
        }
    }

    /// <summary>Request-body serialization alone, exactly as the pipeline produces the JSON content bytes.</summary>
    [Benchmark]
    public byte[] SerializeRequest() => JsonSerializer.SerializeToUtf8Bytes(_request, OpenCodeJsonContext.Default.SessionCreateRequest);

    /// <summary>The complete generated operation sending this request state.</summary>
    [Benchmark]
    public Task<SessionCreateResponse> CreateSessionAsync() => _client!.Sessions.CreateSessionAsync(_request);

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

    private static SessionCreateRequest RequestFor(string state) => state switch
    {
        Absent => new SessionCreateRequest(),
        Cleared => new SessionCreateRequest
        {
            Id = null,
            ParentId = null,
            Title = null,
            Agent = null,
            Model = null,
            Location = null,
            Metadata = null,
            Permissions = null,
        },
        Set => new SessionCreateRequest
        {
            Id = "ses_1",
            ParentId = "ses_0",
            Title = "Fix the build",
            Agent = "build",
            Model = new ModelRef { Id = "sonnet", ProviderId = "anthropic" },
            Location = new LocationPublicRef { Directory = "/repo" },
            Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["run"] = JsonValue("7") },
            Permissions = new[] { new PermissionRule { Action = "edit", Resource = "*", Effect = PermissionEffect.Ask } },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No such request state."),
    };

    private static JsonElement JsonValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static WireFixture Body(string state, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return new WireFixture(state, body, items: 1, payloadBytesPerItem: body.Length);
    }
}
