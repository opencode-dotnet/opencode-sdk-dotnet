using System.IO.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Resolves the results argument received by the test executable itself.</summary>
internal sealed class TestResultsDirectory(IFileSystem fileSystem)
{
    public string Resolve(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] == "--results-directory")
            {
                if (index + 1 == arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    throw new ArgumentException("The test results directory requires a value.", nameof(arguments));
                }

                return fileSystem.Path.GetFullPath(arguments[index + 1]);
            }

            const string prefix = "--results-directory=";
            if (arguments[index].StartsWith(prefix, StringComparison.Ordinal))
            {
                return fileSystem.Path.GetFullPath(arguments[index][prefix.Length..]);
            }
        }

        return fileSystem.Path.GetFullPath("test-results");
    }
}
