using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Commands;

internal sealed partial class GenerateCommand : AsyncCommand<GenerateCommand.Settings>
{
    private readonly IAnsiConsole _console;
    private readonly GenerationCoordinator _coordinator;
    private readonly ILogger<GenerateCommand> _logger;

    public GenerateCommand(IAnsiConsole console, GenerationCoordinator coordinator, ILogger<GenerateCommand> logger)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(logger);

        _console = console;
        _coordinator = coordinator;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        LogInvocation(_logger);

        var generationRequest = GenerationRequest.RepositoryDefault(settings.Verify);
        var report = await _coordinator.GenerateAsync(generationRequest, cancellationToken).ConfigureAwait(false);

        WriteSummary(report);

        if (settings.Verify && report.WriteResult.HasChanges)
        {
            _console.MarkupLine("[red]Generated output was stale and has been repaired:[/]");
            WritePaths(report.WriteResult);
            return 1;
        }

        _console.MarkupLine(settings.Verify
            ? "[green]Generated output is current.[/]"
            : "[green]Generated output updated.[/]");
        return 0;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Generate command invoked.")]
    private static partial void LogInvocation(ILogger logger);

    private void WriteSummary(GenerationReport report)
    {
        _console.MarkupLine($"[grey]Selected operations:[/] {report.SelectedOperationIds.Count.ToString(CultureInfo.InvariantCulture)}");
        _console.MarkupLine($"[yellow]Pending operations:[/] {report.PendingOperationIds.Count.ToString(CultureInfo.InvariantCulture)}");
        _console.MarkupLine($"[grey]Declined operations:[/] {report.DeclinedOperationIds.Count.ToString(CultureInfo.InvariantCulture)}");
        _console.MarkupLine($"[grey]Transport-owned operations:[/] {report.TransportOwnedOperationIds.Count.ToString(CultureInfo.InvariantCulture)}");
    }

    private void WritePaths(WriteResult result)
    {
        foreach (var path in result.CreatedPaths)
        {
            _console.MarkupLine($"[green]+[/] {Markup.Escape(path)}");
        }

        foreach (var path in result.ChangedPaths)
        {
            _console.MarkupLine($"[yellow]~[/] {Markup.Escape(path)}");
        }

        foreach (var path in result.DeletedPaths)
        {
            _console.MarkupLine($"[red]-[/] {Markup.Escape(path)}");
        }
    }

    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--verify")]
        [Description("Return a nonzero exit code when generated output was stale before this run.")]
        public bool Verify { get; init; }
    }
}
