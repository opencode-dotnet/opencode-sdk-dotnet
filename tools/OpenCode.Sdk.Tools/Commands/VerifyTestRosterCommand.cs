using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.TestRoster;
using OpenCode.Sdk.Tools.TestRoster.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Commands;

internal sealed partial class VerifyTestRosterCommand : AsyncCommand<VerifyTestRosterCommand.Settings>
{
    private readonly IAnsiConsole _console;
    private readonly TestRosterLoader _rosterLoader;
    private readonly TrxRunReader _trxRunReader;
    private readonly ILogger<VerifyTestRosterCommand> _logger;

    public VerifyTestRosterCommand(
        IAnsiConsole console,
        TestRosterLoader rosterLoader,
        TrxRunReader trxRunReader,
        ILogger<VerifyTestRosterCommand> logger)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(rosterLoader);
        ArgumentNullException.ThrowIfNull(trxRunReader);
        ArgumentNullException.ThrowIfNull(logger);

        _console = console;
        _rosterLoader = rosterLoader;
        _trxRunReader = trxRunReader;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        LogInvocation(_logger, settings.RosterPath, settings.PassName, settings.OperatingSystem, settings.ResultsDirectory);

        try
        {
            var roster = await _rosterLoader.LoadAsync(settings.RosterPath, cancellationToken).ConfigureAwait(false);
            var expectedModules = roster.ResolveEntries(settings.PassName, settings.OperatingSystem);
            var runs = await _trxRunReader.ReadAsync(settings.ResultsDirectory, cancellationToken).ConfigureAwait(false);
            var comparison = TestRosterComparator.Compare(expectedModules, runs);

            if (!comparison.Passed)
            {
                WriteFailures(comparison);
                return 1;
            }

            WriteRows(comparison);
            _console.MarkupLine(
                $"[green]Test roster verified:[/] {Count(comparison.Rows.Count)} module(s) for pass '{settings.PassName}'");
            return 0;
        }
        catch (TestRosterException exception)
        {
            _console.MarkupLine($"[red]Refused:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Verify-test-roster invoked for roster '{RosterPath}', pass '{PassName}', OS '{OperatingSystem}', results '{ResultsDirectory}'.")]
    private static partial void LogInvocation(
        ILogger logger,
        string rosterPath,
        string passName,
        string operatingSystem,
        string resultsDirectory);

    /// <summary>Every offender is named in one report: the pass's TRX files stay on the runner, so
    /// the console output is the only place the missing coverage is visible.</summary>
    private void WriteFailures(TestRosterComparison comparison)
    {
        _console.MarkupLine("[red]The results do not match the test roster:[/]");
        foreach (var failure in comparison.Failures)
        {
            _console.MarkupLine($"[red]-[/] {Markup.Escape(failure)}");
        }
    }

    private void WriteRows(TestRosterComparison comparison)
    {
        var table = new Table();
        table.AddColumn("Module");
        table.AddColumn(new TableColumn("Executed").RightAligned());
        foreach (var row in comparison.Rows)
        {
            table.AddRow(Markup.Escape(row.Module), Count(row.Executed));
        }

        _console.Write(table);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal sealed class Settings : GlobalSettings
    {
        /// <summary>
        /// Gets the roster path. The two-argument attribute marks each option required: in this
        /// Spectre version a template's angle brackets alone leave an option optional.
        /// </summary>
        [CommandOption("--roster <PATH>", true)]
        [Description("Path to the committed expected roster (tests/test-roster.json).")]
        public string RosterPath { get; init; } = string.Empty;

        [CommandOption("--pass <NAME>", true)]
        [Description("The roster pass to verify, for example 'main' or 'service-elections'.")]
        public string PassName { get; init; } = string.Empty;

        [CommandOption("--os <NAME>", true)]
        [Description("The GitHub runner.os value for this leg (Windows, Linux, or macOS); matched case-insensitively.")]
        public string OperatingSystem { get; init; } = string.Empty;

        [CommandOption("--results <DIRECTORY>", true)]
        [Description("The pass's TRX results directory; only its direct *.trx files are read, subfolders are ignored.")]
        public string ResultsDirectory { get; init; } = string.Empty;
    }
}
