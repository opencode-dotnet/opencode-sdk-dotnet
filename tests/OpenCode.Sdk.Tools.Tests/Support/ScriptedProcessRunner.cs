using System.Text;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// An ordered, strict process fake: every expected invocation is scripted up front, each run
/// consumes the next entry (asserting the file name, every argument token and working directory), and an entry's side
/// effect stands in for what the real process would have written to disk.
/// </summary>
internal sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly List<(string FileName, Func<IReadOnlyList<string>> Arguments, string Directory, ProcessResult Result,
        Func<Task>? SideEffect, IReadOnlyDictionary<string, string?> Environment)> _script = [];
    private int _next;

    public List<string> Invocations { get; } = [];
    public List<CancellationToken> CancellationTokens { get; } = [];

    public ScriptedProcessRunner Expect(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        ProcessResult? result = null, Func<Task>? sideEffect = null, IReadOnlyDictionary<string, string?>? environment = null) =>
        Expect(fileName, () => arguments, workingDirectory, result, sideEffect, environment);

    public ScriptedProcessRunner Expect(string fileName, Func<IReadOnlyList<string>> arguments, string workingDirectory,
        ProcessResult? result = null, Func<Task>? sideEffect = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        _script.Add((fileName, arguments, workingDirectory, result ?? Ok(), sideEffect, environment ?? new Dictionary<string, string?>(StringComparer.Ordinal)));
        return this;
    }

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken) => RunAsync(fileName, arguments, workingDirectory, new Dictionary<string, string?>(StringComparer.Ordinal), cancellationToken);

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentVariables, CancellationToken cancellationToken)
    {
        var invocation = $"{fileName} {string.Join(' ', arguments)}";
        Invocations.Add(invocation);
        CancellationTokens.Add(cancellationToken);
        if (_next >= _script.Count)
        {
            throw new InvalidOperationException($"Unexpected process invocation: {invocation}");
        }

        var (expectedFileName, expectedArguments, expectedDirectory, result, sideEffect, environment) = _script[_next];
        _next++;
        var tokens = expectedArguments();
        if (!string.Equals(fileName, expectedFileName, StringComparison.Ordinal)
            || !arguments.SequenceEqual(tokens, StringComparer.Ordinal)
            || !string.Equals(workingDirectory, expectedDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Process invocation '{invocation}' in '{workingDirectory}' did not match "
                + $"the scripted '{expectedFileName} {string.Join(' ', tokens)}' in '{expectedDirectory}'.");
        }

        if (environment.Count != environmentVariables.Count
            || environment.Any(pair => !environmentVariables.TryGetValue(pair.Key, out var value)
                                       || !string.Equals(pair.Value, value, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Environment overrides for '{invocation}' did not match the script.");
        }

        if (sideEffect is not null)
        {
            await sideEffect().ConfigureAwait(false);
        }

        return result;
    }

    public static ProcessResult Ok(string standardOutputText = "") =>
        new()
        {
            ExitCode = 0,
            StandardOutput = Encoding.UTF8.GetBytes(standardOutputText),
            StandardError = string.Empty,
        };

    public static ProcessResult Ok(byte[] standardOutput) =>
        new()
        {
            ExitCode = 0,
            StandardOutput = standardOutput,
            StandardError = string.Empty,
        };

    public static ProcessResult Fail(string standardError) =>
        new()
        {
            ExitCode = 1,
            StandardOutput = [],
            StandardError = standardError,
        };
}
