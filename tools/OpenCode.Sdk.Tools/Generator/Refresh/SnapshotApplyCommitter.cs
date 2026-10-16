using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Installs a validated plan in fixed order; independent Git and file writes are not atomic.</summary>
internal sealed class SnapshotApplyCommitter(IFileSystem fileSystem, IProcessRunner processRunner)
{
    public async Task<SnapshotReceipt> CommitAsync(SnapshotApplyPlan plan, CancellationToken cancellationToken)
    {
        _ = await processRunner.RunCheckedAsync("git", ["checkout", "--detach", plan.Receipt.UpstreamCommit],
            fileSystem.Path.GetFullPath(SnapshotPaths.Submodule), "moving the submodule checkout", cancellationToken).ConfigureAwait(false);

        await fileSystem.File.WriteAllBytesAsync(SnapshotPaths.AcceptedDocument, plan.Document, cancellationToken).ConfigureAwait(false);
        await fileSystem.File.WriteAllTextAsync(SnapshotPaths.SnapshotMarkdown, plan.Markdown, cancellationToken).ConfigureAwait(false);
        if (plan.SourceWatch is not null)
        {
            await fileSystem.File.WriteAllTextAsync(SnapshotPaths.SourceWatch, plan.SourceWatch, cancellationToken).ConfigureAwait(false);
        }

        // Receipt last: never publish evidence before the payloads it describes.
        await fileSystem.File.WriteAllTextAsync(SnapshotPaths.CommittedReceipt, plan.ReceiptText, cancellationToken).ConfigureAwait(false);
        return plan.Receipt;
    }
}
