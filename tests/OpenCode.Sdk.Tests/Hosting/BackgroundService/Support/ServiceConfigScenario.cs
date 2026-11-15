using System.Text;
using System.Text.Json;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;

/// <summary>A fresh configuration store on the canonical fake, with a recording seam decorator.</summary>
internal sealed class ServiceConfigScenario
{
    private readonly FixtureLoader _fixtures = new();

    public ServiceConfigScenario(string? channel = "local")
    {
        Root = Files.Path.Combine(Files.Path.GetTempPath(), "service-config");
        _ = Files.Directory.CreateDirectory(Root);
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(Files.Path.Combine(Root, "config"));
        Environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(Files.Path.Combine(Root, "state"));
        Adapter = new TestablyServiceFileSystem(Files);
        FileSystem.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Adapter.TryReadAllBytesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        FileSystem.Exists(Arg.Any<string>()).Returns(call => Adapter.Exists(call.Arg<string>()));
        FileSystem.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => Adapter.TryCreateExclusiveAsync(call.ArgAt<string>(0), call.ArgAt<ReadOnlyMemory<byte>>(1), call.ArgAt<CancellationToken>(2)));
        FileSystem.TryDelete(Arg.Any<string>()).Returns(call => Adapter.TryDelete(call.Arg<string>()));
        FileSystem.When(files => files.Rename(Arg.Any<string>(), Arg.Any<string>()))
            .Do(call => Adapter.Rename(call.ArgAt<string>(0), call.ArgAt<string>(1)));
        Options = new OpenCodeServiceConfigOptions { Channel = channel };
        Paths = new ServicePathResolver(Environment).Resolve(ServiceSelection.Snapshot(Options));
        ClearCalls();
    }

    public MockFileSystem Files { get; } = new();
    public IServiceEnvironment Environment { get; } = Substitute.For<IServiceEnvironment>();
    public IServiceFileSystem FileSystem { get; } = Substitute.For<IServiceFileSystem>();
    public TestablyServiceFileSystem Adapter { get; }
    public OpenCodeServiceConfigOptions Options { get; }
    public ServicePaths Paths { get; }
    public string Root { get; }
    public ServiceConfigStore Store => new(Environment, FileSystem);

    public string? Initial(string name)
    {
        using var corpus = JsonDocument.Parse(_fixtures.LoadJson("BackgroundService.service-config-corpus.json"));
        return corpus.RootElement.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name)
            .GetProperty("initial").GetString();
    }

    public async Task SeedAsync(string path, string content)
    {
        _ = Files.Directory.CreateDirectory(Files.Path.GetDirectoryName(path)!);
        await Assert.That(await Adapter.TryCreateExclusiveAsync(path, Encoding.UTF8.GetBytes(content), CancellationToken.None)).IsTrue();
    }

    public async Task<string> SeedDonorAsync(string name)
    {
        var initial = Initial(name)!;
        await SeedAsync(Paths.LegacyConfigFile!, initial);
        return initial;
    }

    public async Task AssertUnchangedAsync(string path, string initial, IReadOnlyList<string> inventory)
    {
        await Assert.That(Inventory()).IsEquivalentTo(inventory, CollectionOrdering.Matching);
        await Assert.That(await Adapter.TryReadAllBytesAsync(path, CancellationToken.None))
            .IsEquivalentTo(Encoding.UTF8.GetBytes(initial), CollectionOrdering.Matching);
    }

    public IReadOnlyList<string> Inventory() =>
        [.. Files.Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Select(static path => "file:" + path)
            .Concat(Files.Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).Select(static path => "directory:" + path))
            .OrderBy(static path => path, StringComparer.Ordinal)];

    public IReadOnlyList<string> Calls => [.. FileSystem.ReceivedCalls()
        .Select(static call => call.GetMethodInfo().Name + ":" + call.GetArguments()[0])];

    public void ClearCalls()
    {
        FileSystem.ClearReceivedCalls();
        Environment.ClearReceivedCalls();
    }
}
