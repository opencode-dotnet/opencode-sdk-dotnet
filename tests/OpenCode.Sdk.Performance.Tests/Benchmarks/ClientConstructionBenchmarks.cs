using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>Client construction and disposal, directly and through a host's fresh DI root; no network.</summary>
[MemoryDiagnoser]
public class ClientConstructionBenchmarks
{
    private OpenCodeClientOptions _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        _options = new OpenCodeClientOptions
        {
            Endpoint = new Uri("https://benchmark.invalid"),
            Password = "benchmark",
        };
    }

    /// <summary>Constructs and disposes the owned-transport client with options prepared once in setup.</summary>
    [Benchmark]
    public void ConstructAndDisposeClient() => new OpenCodeClient(_options).Dispose();

    /// <summary>Registers, configures, builds, first-resolves the singleton client, and disposes the provider.</summary>
    [Benchmark]
    public OpenCodeClient RegisterBuildResolveClientAndDisposeProvider()
    {
        var services = new ServiceCollection();
        _ = services.AddOpenCode(options =>
        {
            options.Endpoint = _options.Endpoint;
            options.Password = _options.Password;
        });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<OpenCodeClient>();
    }
}
