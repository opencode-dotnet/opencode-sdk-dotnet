using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>Client construction and disposal, directly and through a host's fresh DI root; no network.</summary>
[MemoryDiagnoser]
public class ClientConstructionBenchmarks
{
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    /// <summary>Constructs and disposes the owned-transport client with options prepared once.</summary>
    [Benchmark]
    public void ConstructAndDisposeClient() => new OpenCodeClient(Options).Dispose();

    /// <summary>Registers, configures, builds, first-resolves the singleton client, and disposes the provider.</summary>
    [Benchmark]
    public void RegisterBuildResolveClientAndDisposeProvider()
    {
        var services = new ServiceCollection();
        _ = services.AddOpenCode(options =>
        {
            options.Endpoint = Options.Endpoint;
            options.Password = Options.Password;
        });
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<OpenCodeClient>();
    }
}
