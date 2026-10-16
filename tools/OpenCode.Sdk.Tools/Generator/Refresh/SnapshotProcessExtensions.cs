using System.Globalization;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Converts a checked refresh process failure into the existing refusal diagnostic.</summary>
internal static class SnapshotProcessExtensions
{
    public static async Task<ProcessResult> RunCheckedAsync(this IProcessRunner runner, string fileName,
        IReadOnlyList<string> arguments, string workingDirectory, string description, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(fileName, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not 0)
        {
            var detail = result.StandardError.Trim();
            throw new SnapshotRefreshException($"{description} failed ({fileName} {string.Join(' ', arguments)}, exit "
                + $"{result.ExitCode.ToString(CultureInfo.InvariantCulture)}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
        }

        return result;
    }
}
