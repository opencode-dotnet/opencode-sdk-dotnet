using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Commands;

internal sealed partial class RefreshSpecCommand : AsyncCommand<RefreshSpecCommand.Settings>
{
    private readonly IAnsiConsole _console;
    private readonly ILogger<RefreshSpecCommand> _logger;
    private readonly SnapshotSynchronizer _synchronizer;

    public RefreshSpecCommand(IAnsiConsole console, SnapshotSynchronizer synchronizer, ILogger<RefreshSpecCommand> logger)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(synchronizer);
        ArgumentNullException.ThrowIfNull(logger);

        _console = console;
        _synchronizer = synchronizer;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        LogInvocation(_logger);
        if (settings.Reference is { } reference)
        {
            return await PrepareAsync(reference, cancellationToken).ConfigureAwait(false);
        }

        if (settings.Apply is { } receiptPath)
        {
            return await ApplyAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        }

        return await VerifyAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Refresh-spec command invoked.")]
    private static partial void LogInvocation(ILogger logger);

    private async Task<int> PrepareAsync(string reference, CancellationToken cancellationToken)
    {
        var outcome = await _synchronizer.PrepareAsync(reference, cancellationToken).ConfigureAwait(false);
        var receipt = outcome.Receipt;
        _console.MarkupLine($"[grey]Upstream commit:[/] {Markup.Escape(receipt.UpstreamCommit)}");
        WriteReceiptSummary(receipt);
        WriteOwnerSummary(outcome);
        _console.MarkupLine($"[grey]Normalized document:[/] {Markup.Escape(outcome.NormalizedDocumentPath)}");
        _console.MarkupLine($"[green]Receipt prepared:[/] {Markup.Escape(outcome.ReceiptPath)}");
        _console.MarkupLine("[grey]Review the receipt, then apply it with[/] refresh-spec --apply <receipt>.");
        return 0;
    }

    private async Task<int> VerifyAsync(CancellationToken cancellationToken)
    {
        var outcome = await _synchronizer.VerifyAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.IsReproduced)
        {
            _console.MarkupLine($"[green]The accepted snapshot reproduces its receipt[/] ({Markup.Escape(outcome.UpstreamCommit)}).");
            return 0;
        }

        _console.MarkupLine("[red]The accepted snapshot does not reproduce its receipt:[/]");
        foreach (var problem in outcome.Problems)
        {
            _console.MarkupLine($"[red]-[/] {Markup.Escape(problem)}");
        }

        return 1;
    }

    private async Task<int> ApplyAsync(string receiptPath, CancellationToken cancellationToken)
    {
        var receipt = await _synchronizer.ApplyAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        WriteReceiptSummary(receipt);
        _console.MarkupLine($"[green]Accepted snapshot applied:[/] {Markup.Escape(receipt.UpstreamCommit)}");
        _console.MarkupLine("[grey]Nothing was staged or committed; review the diff, regenerate, and run the gates.[/]");
        return 0;
    }

    private void WriteReceiptSummary(SnapshotReceipt receipt)
    {
        _console.MarkupLine($"[grey]Operations:[/] {receipt.OperationCount.ToString(CultureInfo.InvariantCulture)} "
                            + $"([green]+{receipt.AddedOperations.Count.ToString(CultureInfo.InvariantCulture)}[/] "
                            + $"[red]-{receipt.RemovedOperations.Count.ToString(CultureInfo.InvariantCulture)}[/])");
        _console.MarkupLine($"[grey]Components:[/] {receipt.ComponentCount.ToString(CultureInfo.InvariantCulture)}; "
                            + $"[grey]contentSchema occurrences:[/] {receipt.ContentSchemaCount.ToString(CultureInfo.InvariantCulture)}");
        _console.MarkupLine(receipt.Patches.Count is 0
            ? "[grey]Patch list:[/] empty (the pinned generator's own output)"
            : $"[yellow]Patch list:[/] {receipt.Patches.Count.ToString(CultureInfo.InvariantCulture)} Restore patch(es) applied");
        _console.MarkupLine(string.Equals(receipt.RawDocumentSha256, receipt.GeneratedBaselineSha256, StringComparison.Ordinal)
            ? "[grey]Upstream's committed document:[/] matches its generator"
            : "[yellow]Upstream's committed document:[/] differs from its generator (stale upstream artifact; not consumed)");
        WriteWatchedSourceSummary(receipt);
    }

    private void WriteWatchedSourceSummary(SnapshotReceipt receipt)
    {
        if (receipt.WatchedSources.Count is 0)
        {
            _console.MarkupLine("[grey]Watched sources:[/] none");
            return;
        }

        var lost = receipt.WatchedSources.Sum(static source => source.Anchors.Count(static anchor => !anchor.Matched));
        var count = receipt.WatchedSources.Count.ToString(CultureInfo.InvariantCulture);
        _console.MarkupLine(lost is 0
            ? $"[grey]Watched sources:[/] {count}, every anchor still matches"
            : $"[yellow]Watched sources:[/] {count}, {lost.ToString(CultureInfo.InvariantCulture)} lost anchor(s) — read the doors");
    }

    private void WriteOwnerSummary(PrepareOutcome outcome)
    {
        var observed = outcome.Receipt.WatchedSources.ToDictionary(static source => source.Path, StringComparer.Ordinal);
        foreach (var owner in new[] { WatchOwners.Sdk, WatchOwners.AgentsAi })
        {
            var moved = outcome.PinnedSources.Where(source => !string.Equals(source.Sha256, observed[source.Path].Sha256, StringComparison.Ordinal)
                    && source.Behaviors.Any(behavior => string.Equals(behavior.Owner, owner, StringComparison.Ordinal)))
                .Select(static source => source.Path).ToArray();
            if (moved.Length > 0)
            {
                _console.WriteLine($"Moved for {owner}: {string.Join(", ", moved)}");
            }
        }

        foreach (var source in outcome.PinnedSources)
        {
            foreach (var behavior in source.Behaviors.Where((_, index) => !observed[source.Path].Anchors[index].Matched))
            {
                _console.WriteLine($"Lost anchor for {behavior.Owner}: {source.Path} ('{behavior.Anchor.Text}')");
            }
        }
    }

    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--ref <REF>")]
        [Description("Prepare a snapshot candidate from this commit-ish; writes only scratch artifacts.")]
        public string? Reference { get; init; }

        [CommandOption("--verify")]
        [Description("Reproduce the accepted snapshot's committed receipt observationally.")]
        public bool Verify { get; init; }

        [CommandOption("--apply <RECEIPT>")]
        [Description("Apply one reviewed receipt to the accepted snapshot paths; never stages or commits.")]
        public string? Apply { get; init; }

        public override ValidationResult Validate()
        {
            var modes = (Reference is not null ? 1 : 0) + (Verify ? 1 : 0) + (Apply is not null ? 1 : 0);
            return modes is 1
                ? ValidationResult.Success()
                : ValidationResult.Error("Specify exactly one of --ref <REF>, --verify, or --apply <RECEIPT>.");
        }
    }
}
