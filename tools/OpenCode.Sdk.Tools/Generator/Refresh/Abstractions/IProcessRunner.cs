namespace OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

/// <summary>Runs external processes for repository tooling, capturing stdout bytes and stderr text.</summary>
internal interface IProcessRunner
{
    /// <summary>Runs the process to completion, capturing stdout bytes and stderr text; never throws on a nonzero exit.</summary>
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken);

    /// <summary>Runs with explicit child environment overrides; legacy adapters support only an empty map.</summary>
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentVariables, CancellationToken cancellationToken) =>
        environmentVariables.Count is 0
            ? RunAsync(fileName, arguments, workingDirectory, cancellationToken)
            : throw new NotSupportedException("This process runner does not support environment overrides.");
}
