using System.ComponentModel;
using System.Globalization;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Clones;
using OpenCode.Sdk.Tools.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Commands;

/// <summary>
/// The clone gate: runs the pinned jscpd over every <see cref="CloneScopes"/> scope from the
/// repository root, prints each scope's totals, and fails when a gated scope finds a clone its
/// baseline does not hold or crosses its threshold. A scope that only reports carries no gate
/// arguments, so a nonzero exit there is a failed run, and it fails the gate too. Every scope runs
/// and prints before the verdict.
/// </summary>
internal sealed partial class VerifyClonesCommand : AsyncCommand<VerifyClonesCommand.Settings>
{
    private const string SolutionFileName = "OpenCode.slnx";

    private readonly IFileSystem _fileSystem;
    private readonly IAnsiConsole _console;
    private readonly CloneDetector _detector;
    private readonly ILogger<VerifyClonesCommand> _logger;

    public VerifyClonesCommand(IFileSystem fileSystem, IAnsiConsole console, CloneDetector detector, ILogger<VerifyClonesCommand> logger)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(logger);

        _fileSystem = fileSystem;
        _console = console;
        _detector = detector;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        var root = _fileSystem.Directory.GetCurrentDirectory();
        LogInvocation(_logger, root, settings.UpdateBaseline);
        var work = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "opencode-clones-" + Guid.NewGuid().ToString("N"));
        try
        {
            Prepare(root, settings.UpdateBaseline);
            var split = await _detector.ReadSourceSplitAsync(root, cancellationToken).ConfigureAwait(false);
            _fileSystem.Directory.CreateDirectory(work);
            var failed = new List<CloneScopeResult>();
            foreach (var scope in CloneScopes.All)
            {
                var result = await _detector.RunAsync(root, scope, split, work, settings.UpdateBaseline, cancellationToken).ConfigureAwait(false);
                KeepReport(work, scope, settings.ReportDirectory);
                _console.MarkupLineUnwrapped(Markup.Escape(Summary(result)));
                if (result.ExitCode != 0)
                {
                    failed.Add(result);
                }
            }

            if (failed.Count > 0)
            {
                foreach (var result in failed)
                {
                    _console.MarkupLineUnwrapped($"[red]Clone gate failed for '{Markup.Escape(result.Scope.Name)}':[/] {Markup.Escape(Verdict(result.StandardError))}");
                    foreach (var location in result.NewCloneLocations)
                    {
                        _console.MarkupLineUnwrapped(Markup.Escape(Location(location)));
                    }
                }

                return 1;
            }

            _console.MarkupLineUnwrapped(settings.UpdateBaseline ? "[green]Clone baselines updated.[/]" : "[green]Clone gate passed.[/]");
            return 0;
        }
        finally
        {
            if (_fileSystem.Directory.Exists(work))
            {
                _fileSystem.Directory.Delete(work, recursive: true);
            }
        }
    }

    /// <summary>One scope's line: its clones, duplicated and scanned lines, percentage, and for a gated scope the clones its baseline does not hold.</summary>
    internal static string Summary(CloneScopeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Scope.Name}: {result.Clones} clones, {result.DuplicatedLines} of {result.Lines} lines ({result.Percentage:0.00}%)");
        return result.Scope.IsGated
            ? string.Create(CultureInfo.InvariantCulture, $"{line}, {result.NewClones} new")
            : line + ", reported only";
    }

    /// <summary>One new clone's line: its size and both fragments, so the author can find it without running jscpd.</summary>
    internal static string Location(CloneLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        return string.Create(CultureInfo.InvariantCulture, $"  new clone ({location.Tokens} tokens): {location.First} <-> {location.Second}");
    }

    /// <summary>Copies a scope's report into the directory the caller asked for, named after the scope, so CI can keep it as an artifact.</summary>
    private void KeepReport(string work, CloneScope scope, string? reportDirectory)
    {
        if (string.IsNullOrEmpty(reportDirectory))
        {
            return;
        }

        _fileSystem.Directory.CreateDirectory(reportDirectory);
        _fileSystem.File.Copy(_detector.ReportPath(work, scope), _fileSystem.Path.Combine(reportDirectory, scope.Name + ".json"), overwrite: true);
    }

    /// <summary>jscpd's own verdict lines from its standard error (it also logs which config it read there), or all of it when it gave none.</summary>
    private static string Verdict(string standardError)
    {
        var errors = standardError.Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("ERROR", StringComparison.Ordinal))
            .ToList();
        return errors.Count > 0 ? string.Join(' ', errors) : standardError;
    }

    private void Prepare(string root, bool updateBaseline)
    {
        if (!_fileSystem.File.Exists(_fileSystem.Path.Combine(root, SolutionFileName)))
        {
            throw new CloneDetectorException($"Run verify-clones from the repository root: no {SolutionFileName} in '{root}'.");
        }

        _detector.EnsureInstalled(root);
        if (updateBaseline)
        {
            return;
        }

        var missing = CloneScopes.All.FirstOrDefault(scope =>
            scope.Baseline is not null && !_fileSystem.File.Exists(_fileSystem.Path.Combine(root, scope.Baseline)));
        if (missing is not null)
        {
            throw new CloneDetectorException(
                $"The '{missing.Name}' clone baseline is missing at '{missing.Baseline}'; run verify-clones --update-baseline and commit the baseline.");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Verify-clones invoked at '{Root}', update baseline {UpdateBaseline}.")]
    private static partial void LogInvocation(ILogger logger, string root, bool updateBaseline);

    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--update-baseline")]
        [Description("Rewrite the gated scopes' committed baselines from this run instead of failing on new clones.")]
        public bool UpdateBaseline { get; init; }

        [CommandOption("--report-directory <DIRECTORY>")]
        [Description("Also keep each scope's jscpd JSON report in this directory, as <scope>.json, for example to upload it from CI.")]
        public string? ReportDirectory { get; init; }
    }
}
