using BenchmarkDotNet.Running;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests;

internal static class Program
{
    private static void Main(string[] args)
    {
        var net11 = args.Contains(BenchmarkConfig.Net11Switch, StringComparer.Ordinal);
        string[] forwarded = [.. args.Where(static argument => !string.Equals(argument, BenchmarkConfig.Net11Switch, StringComparison.Ordinal))];
        _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(forwarded, BenchmarkConfig.Create(net11));
    }
}
