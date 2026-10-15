using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using BenchmarkDotNet.Validators;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// BenchmarkDotNet's own project toolchain for <c>net11.0</c>, without its SDK-version validator:
/// BenchmarkDotNet 0.15.8 cannot compute the SDK version a runtime it does not know needs and throws
/// from that validator (<c>GetRuntimeVersion not implemented for NotRecognized</c>), which ends the
/// whole run before any leg starts. Generating, building and running stay BenchmarkDotNet's; a host
/// without a .NET 11 SDK fails at the build instead of at validation.
/// </summary>
internal sealed class Net11Toolchain : IToolchain
{
    private readonly IToolchain _inner = CsProjCoreToolchain.From(new NetCoreAppSettings("net11.0", runtimeFrameworkVersion: null, name: ".NET 11.0"));

    public string Name => _inner.Name;

    public IGenerator Generator => _inner.Generator;

    public IBuilder Builder => _inner.Builder;

    public IExecutor Executor => _inner.Executor;

    public bool IsInProcess => _inner.IsInProcess;

    public IEnumerable<ValidationError> Validate(BenchmarkCase benchmarkCase, IResolver resolver) => [];
}
