using System.ComponentModel;
using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Benchmarks;
using OpenCode.Sdk.Tools.Benchmarks.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Commands;

internal sealed partial class CompareBenchmarksCommand : AsyncCommand<CompareBenchmarksCommand.Settings>
{
    private readonly IAnsiConsole _console;
    private readonly IFileSystem _fileSystem;
    private readonly BenchmarkRunReader _runReader;
    private readonly ILogger<CompareBenchmarksCommand> _logger;

    public CompareBenchmarksCommand(
        IAnsiConsole console,
        IFileSystem fileSystem,
        BenchmarkRunReader runReader,
        ILogger<CompareBenchmarksCommand> logger)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(runReader);
        ArgumentNullException.ThrowIfNull(logger);

        _console = console;
        _fileSystem = fileSystem;
        _runReader = runReader;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        LogInvocation(_logger, settings.BeforePath, settings.AfterPath);

        var before = await _runReader.ReadAsync(settings.BeforePath, cancellationToken).ConfigureAwait(false);
        var after = await _runReader.ReadAsync(settings.AfterPath, cancellationToken).ConfigureAwait(false);
        var comparison = BenchmarkComparisonComposer.Compose(before, after);
        if (comparison.Rows.Count == 0)
        {
            WriteZeroOverlapFailure(before, after);
            return 1;
        }

        WriteRuntimeLegMismatchWarning(before, after);
        WriteRows(comparison);
        WriteUnmatched(comparison);
        _console.MarkupLine($"[grey]Compared cases:[/] {Count(comparison.Rows.Count)}");

        if (settings.OutputPath is { } outputPath)
        {
            await WriteCsvAsync(outputPath, comparison, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Compare-benchmarks invoked for '{BeforePath}' vs '{AfterPath}'.")]
    private static partial void LogInvocation(ILogger logger, string beforePath, string afterPath);

    /// <summary>Zero overlap between non-empty runs is a mislabelled leg, not an empty result: a run
    /// launched without <c>--runtimes</c> is labelled by its job name (such as <c>DefaultJob</c>) and
    /// joins nothing against a runtime-labelled baseline. Name both label sets so the cause is
    /// visible instead of printing an all-one-sided comparison.</summary>
    private void WriteZeroOverlapFailure(IReadOnlyList<BenchmarkRunCase> before, IReadOnlyList<BenchmarkRunCase> after)
    {
        _console.MarkupLine(
            $"[red]The runs share no benchmark cases[/] ({Count(before.Count)} before, {Count(after.Count)} after).");
        _console.MarkupLine($"[red]before runtimes:[/] {Markup.Escape(RuntimeLabels(before))}");
        _console.MarkupLine($"[red]after runtimes:[/] {Markup.Escape(RuntimeLabels(after))}");
        _console.MarkupLine(
            "[red]Cases join on (case, runtime). A job-named leg such as 'DefaultJob' means that run was launched "
            + "without --runtimes; relaunch it with --runtimes net10.0 net472 to make it joinable.[/]");
    }

    /// <summary>A partial form of the zero-overlap failure: when the runs' runtime label sets
    /// differ, every case on a leg only one side has is silently one-sided, so say so once.</summary>
    private void WriteRuntimeLegMismatchWarning(IReadOnlyList<BenchmarkRunCase> before, IReadOnlyList<BenchmarkRunCase> after)
    {
        var beforeLabels = RuntimeLabels(before);
        var afterLabels = RuntimeLabels(after);
        if (string.Equals(beforeLabels, afterLabels, StringComparison.Ordinal))
        {
            return;
        }

        _console.MarkupLine(
            $"[yellow]warning:[/] the runs carry different runtime legs (before: {Markup.Escape(beforeLabels)}; "
            + $"after: {Markup.Escape(afterLabels)}); every case on a leg only one side has is one-sided.");
    }

    private static string RuntimeLabels(IReadOnlyList<BenchmarkRunCase> cases) =>
        string.Join(", ", cases
            .Select(static runCase => $"'{runCase.Runtime}'")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    private void WriteRows(BenchmarkComparison comparison)
    {
        var table = new Table();
        table.AddColumn("Case");
        table.AddColumn("Runtime");
        table.AddColumn(new TableColumn("Wire (B)").RightAligned());
        table.AddColumn(new TableColumn("Items").RightAligned());
        table.AddColumn(new TableColumn("Payload (B/item)").RightAligned());
        table.AddColumn(new TableColumn("Alloc before (B)").RightAligned());
        table.AddColumn(new TableColumn("Alloc after (B)").RightAligned());
        table.AddColumn(new TableColumn("Delta (B)").RightAligned());
        table.AddColumn(new TableColumn("Time ratio").RightAligned());
        foreach (var row in comparison.Rows)
        {
            table.AddRow(
                Markup.Escape(row.CaseLabel),
                Markup.Escape(row.Runtime),
                // "-" marks a case without wire figures: no wire fixture, or runs predating them.
                OptionalCount(row.WireBytes),
                OptionalCount(row.WireItems),
                OptionalCount(row.PayloadBytesPerItem),
                row.AllocatedBefore.ToString("N0", CultureInfo.InvariantCulture),
                row.AllocatedAfter.ToString("N0", CultureInfo.InvariantCulture),
                row.AllocatedDelta.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture),
                // "n/a" marks a matched case with no timing ratio (a noise-floor median on a leg);
                // a case absent from the other run is listed as before-only/after-only instead.
                row.TimeRatio is { } timeRatio ? timeRatio.ToString("0.00", CultureInfo.InvariantCulture) : "n/a");
        }

        _console.Write(table);
    }

    private void WriteUnmatched(BenchmarkComparison comparison)
    {
        foreach (var runCase in comparison.BeforeOnly)
        {
            _console.MarkupLine($"[grey]before-only:[/] {Markup.Escape(runCase.CaseLabel)} ({Markup.Escape(runCase.Runtime)})");
        }

        foreach (var runCase in comparison.AfterOnly)
        {
            _console.MarkupLine($"[grey]after-only:[/] {Markup.Escape(runCase.CaseLabel)} ({Markup.Escape(runCase.Runtime)})");
        }
    }

    private async Task WriteCsvAsync(string outputPath, BenchmarkComparison comparison, CancellationToken cancellationToken)
    {
        var outputDirectory = _fileSystem.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            _fileSystem.Directory.CreateDirectory(outputDirectory);
        }

        await _fileSystem.File
            .WriteAllTextAsync(outputPath, BenchmarkComparisonCsvComposer.Compose(comparison), cancellationToken)
            .ConfigureAwait(false);
        _console.MarkupLine($"[green]Comparison written:[/] {Markup.Escape(outputPath)}");
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string OptionalCount(long? value) =>
        value is { } presentValue ? presentValue.ToString("N0", CultureInfo.InvariantCulture) : "-";

    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<BEFORE>")]
        [Description("Baseline benchmark run folder (or its results/ folder).")]
        public string BeforePath { get; init; } = string.Empty;

        [CommandArgument(1, "<AFTER>")]
        [Description("Candidate benchmark run folder (or its results/ folder).")]
        public string AfterPath { get; init; } = string.Empty;

        [CommandOption("--output <PATH>")]
        [Description("Optional path for the comparison CSV extract.")]
        public string? OutputPath { get; init; }
    }
}
