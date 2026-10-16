using BenchmarkDotNet.Running;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        _ = await BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).RunAsync(args, BenchmarkConfig.Create()).ConfigureAwait(false);
    }
}
