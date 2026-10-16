using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Captures accepted artifacts once so workflows validate a single observation.</summary>
internal sealed partial class SnapshotStateReader(IFileSystem fileSystem, IProcessRunner processRunner)
{
    public async Task<SnapshotState> ReadAsync(CancellationToken cancellationToken)
    {
        var document = await fileSystem.File.ReadAllBytesAsync(SnapshotPaths.AcceptedDocument, cancellationToken).ConfigureAwait(false);
        var receipt = await ReadOptionalAsync(SnapshotPaths.CommittedReceipt, cancellationToken).ConfigureAwait(false);
        var markdown = await ReadOptionalAsync(SnapshotPaths.SnapshotMarkdown, cancellationToken).ConfigureAwait(false);
        var watch = await ReadOptionalAsync(SnapshotPaths.SourceWatch, cancellationToken).ConfigureAwait(false);
        var head = await processRunner.RunAsync("git", ["rev-parse", "HEAD"],
            fileSystem.Path.GetFullPath(SnapshotPaths.Submodule), cancellationToken).ConfigureAwait(false);
        if (head.ExitCode is not 0)
        {
            throw new SnapshotRefreshException($"reading the submodule checkout failed (git rev-parse HEAD, exit {head.ExitCode.ToString(CultureInfo.InvariantCulture)}): {head.StandardError.Trim()}");
        }

        return new SnapshotState(document, receipt, markdown, watch, head.StandardOutputText.Trim());
    }

    public async Task<SnapshotReceipt> ReadReceiptAsync(string path, CancellationToken cancellationToken)
    {
        if (!fileSystem.File.Exists(path))
        {
            throw new SnapshotRefreshException($"receipt '{path}' does not exist");
        }

        return ParseReceipt(await fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), path);
    }

    public static SnapshotReceipt ParseReceipt(byte[] bytes, string path)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, ToolJsonContext.Default.SnapshotReceipt)
                ?? throw new SnapshotRefreshException($"receipt '{path}' cannot be JSON null");
        }
        catch (JsonException exception)
        {
            throw new SnapshotRefreshException($"receipt '{path}' is invalid: {exception.Message}", exception);
        }
    }

    public static bool IsFullCommit(string commit) => FullShaRegex().IsMatch(commit);

    public static string SerializeReceipt(SnapshotReceipt receipt) =>
        JsonSerializer.Serialize(receipt, ToolJsonContext.Default.SnapshotReceipt) + "\n";

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FullShaRegex();

    private async Task<byte[]?> ReadOptionalAsync(string path, CancellationToken cancellationToken) =>
        fileSystem.File.Exists(path)
            ? await fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
}
