using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The config read, the widest and deepest model the server returns: the complete generated
/// operation, the pipeline through an adapter that materializes nothing, the generated adapter
/// alone, and source-generated materialization alone, over a project's config and a gateway-sized
/// provider catalog (<see cref="ConfigFixture"/>). Every structural union kind a config carries is
/// read on each call, nested inside dictionaries and lists. The raw-send rung sends and drains the
/// same response with no SDK (<see cref="CannedExchange"/>), the floor a buffered pipeline rung
/// stands on.
/// </summary>
[MemoryDiagnoser]
public class ConfigBenchmarks : IDisposable
{
    private const string OperationId = "config.get";

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private static readonly IReadOnlyList<(WireFixture Fixture, ConfigFixture Shape)> Cases =
    [
        Case("project-1x4", new ConfigFixture(providers: 1, modelsPerProvider: 4)),
        Case("catalog-8x24", new ConfigFixture(providers: 8, modelsPerProvider: 24)),
    ];

    private byte[] _body = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private Pipeline? _pipeline;
    private CannedExchange? _raw;

    public static IEnumerable<WireFixture> Fixtures() => Cases.Select(static entry => entry.Fixture);

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    /// <summary>
    /// Refuses a body that does not materialize every union arm it was built to carry, so no rung
    /// measures an unknown carrier where it claims a typed one.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _body = Fixture.Bytes;
        _handler = new CannedResponseHandler(_body);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _pipeline = new Pipeline(_httpClient, ownsHttpClient: false, Options);
        _raw = new CannedExchange(_httpClient, HttpMethod.Get, Options.Endpoint!);
        await _raw.RequireDrainAsync(_body.Length).ConfigureAwait(false);

        var shape = Cases.First(entry => string.Equals(entry.Fixture.Name, Fixture.Name, StringComparison.Ordinal)).Shape;
        var response = await GetConfigAsync().ConfigureAwait(false);
        if (response.Config is not [ConfigDirectory, ConfigDocument { Info: var info }])
        {
            throw new InvalidOperationException($"Fixture '{Fixture.Name}' did not materialize a directory and a document.");
        }

        RequireCatalog(info, shape);
        Require(info.Model?.Kind is ConfigModelSelectionKind.Text
                && info.Agents?["build"].Model?.Kind is ConfigModelSelectionKind.ConfigModelSelectionExplicit,
            "model selections as text and as objects");
        Require(info.Lsp is { Kind: ConfigInfoLspKind.ConfigLspEntryDictionary } lsp
                && lsp.ConfigLspEntryDictionary["typescript"].Kind is ConfigLspEntryKind.ConfigLspServer
                && lsp.ConfigLspEntryDictionary["eslint"].Kind is ConfigLspEntryKind.ConfigLspDisabled,
            "an LSP server and a disabled LSP entry");
        Require(info.References is { } references
                && references["docs"].Kind is ConfigReferenceEntryKind.Text
                && references["upstream"].Kind is ConfigReferenceEntryKind.ConfigReferenceGit
                && references["sibling"].Kind is ConfigReferenceEntryKind.ConfigReferenceLocal,
            "references as text, a repository and a path");
        Require(info.Plugins is [{ Kind: ConfigInfoPluginsKind.Text }, { Kind: ConfigInfoPluginsKind.ConfigPluginEntry }]
                && info.Formatter is { Kind: ConfigInfoFormatterKind.ConfigFormatterEntryDictionary } formatter
                && formatter.ConfigFormatterEntryDictionary.Count == 2
                && info.Warming?.Kind is ConfigInfoWarmingKind.Boolean,
            "plugins, the formatter map and the warming switch");
    }

    /// <summary>The complete generated operation: request, send, buffer, validate, adapt, materialize.</summary>
    [Benchmark]
    public Task<ConfigResponse> GetConfigAsync() => _client!.Config.GetConfigAsync();

    /// <summary>The same pipeline through a no-op adapter: everything above minus JSON and model cost.</summary>
    [Benchmark]
    public Task<NoOpResponse> ExecuteWithoutAdapterAsync() =>
        _pipeline!.ExecuteAsync(OperationId, HttpMethod.Get, OpenCodeRoutes.Config.GetConfig(), NoOpResponseAdapter.Instance, options: null, CancellationToken.None);

    /// <summary>The generated adapter over validated UTF-8: materialization plus the response envelope.</summary>
    [Benchmark]
    public ConfigResponse AdaptSuccess() => ConfigResponseAdapter.Instance.AdaptSuccess(200, _body);

    /// <summary>Source-generated materialization alone.</summary>
    [Benchmark]
    public IReadOnlyList<IConfigEntry>? Deserialize() => JsonSerializer.Deserialize(_body, OpenCodeJsonContext.Default.IConfigEntryList);

    /// <summary>The harness floor: a bare send through the same handler with the body drained, no SDK.</summary>
    [Benchmark]
    public Task<int> SendRawRequestAsync() => _raw!.SendAndDrainAsync();

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

        _pipeline?.Dispose();
        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    private static (WireFixture Fixture, ConfigFixture Shape) Case(string name, ConfigFixture shape)
    {
        var body = shape.Build();
        return (new WireFixture(name, body, items: 1, payloadBytesPerItem: body.Length), shape);
    }

    /// <summary>Refuses a catalog whose providers or models do not each carry the arms the fixture writes for them.</summary>
    private void RequireCatalog(ConfigInfo info, ConfigFixture shape)
    {
        Require(info.Providers?.Count == shape.Providers, "every provider");
        foreach (var (providerId, provider) in info.Providers!)
        {
            var settings = provider.Settings;
            Require(settings?.Timeout?.Kind is ConfigProviderSettingsTimeoutKind.Number
                    && settings.HeaderTimeout?.Kind is ConfigProviderSettingsHeaderTimeoutKind.Boolean
                    && settings.ChunkTimeout?.Kind is ConfigProviderSettingsChunkTimeoutKind.Number
                    && settings.AdditionalProperties.ContainsKey("baseURL"),
                $"{providerId}'s number and boolean timeouts and its open base URL");
            Require(provider.Models?.Count == shape.ModelsPerProvider, $"{providerId}'s models");
            foreach (var (modelId, model) in provider.Models!)
            {
                RequireModel(providerId, modelId, model);
            }
        }
    }

    /// <summary>Even models price with one cost, odd ones with a context-tiered pair (<see cref="ConfigFixture"/>).</summary>
    private void RequireModel(string providerId, string modelId, ConfigModel model)
    {
        // The model key ends in its index, so its last digit gives the parity.
        var even = (modelId[^1] - '0') % 2 == 0;
        Require(even
                ? model.Cost?.Kind is ConfigModelPricingKind.ConfigModelCost
                : model.Cost is { Kind: ConfigModelPricingKind.ConfigModelCostList } pricing
                  && pricing.ConfigModelCostList is [{ Tier: null }, { Tier.Size: 200000 }],
            $"{providerId}/{modelId}'s {(even ? "single" : "tiered")} pricing");
        Require(model.Settings?.AdditionalProperties.ContainsKey("temperature") == true
                && model.Variants is [{ Id: "high", Settings: not null }, { Id: "low", Settings: not null }],
            $"{providerId}/{modelId}'s open settings and its two variants");
    }

    private void Require(bool holds, string what)
    {
        if (!holds)
        {
            throw new InvalidOperationException($"Fixture '{Fixture.Name}' did not materialize {what}.");
        }
    }
}
