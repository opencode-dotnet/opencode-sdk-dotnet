#if NET
using OpenCode.Sdk.Tools.Output.Abstractions;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>Formatting is measured on its own row, so the run without it leaves the sources as emitted.</summary>
internal sealed class NoOpProjectFormatter : IProjectFormatter
{
    public Task FormatAsync(string projectPath, IReadOnlyList<string> sourcePaths, CancellationToken cancellationToken) => Task.CompletedTask;
}
#endif
