using System.Text;
using CliWrap;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Production process runner over CliWrap, the repository's existing process library.</summary>
internal sealed class CliWrapProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken) => RunAsync(fileName, arguments, workingDirectory,
            new Dictionary<string, string?>(StringComparer.Ordinal), cancellationToken);

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentVariables, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var standardOutput = new MemoryStream();
        var standardError = new StringBuilder();
        var result = await Cli.Wrap(fileName)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(environment => environment.Set(environmentVariables))
            .WithStandardOutputPipe(PipeTarget.ToStream(standardOutput))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ProcessResult
        {
            ExitCode = result.ExitCode,
            StandardOutput = standardOutput.ToArray(),
            StandardError = standardError.ToString(),
        };
    }
}
