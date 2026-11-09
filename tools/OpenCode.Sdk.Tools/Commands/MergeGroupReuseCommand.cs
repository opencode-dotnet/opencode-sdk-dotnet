using System.ComponentModel;
using System.IO.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Commands;

internal sealed class MergeGroupReuseCommand(IFileSystem fileSystem, IAnsiConsole console)
    : AsyncCommand<MergeGroupReuseCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        var reason = "unreadable facts";
        try
        {
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "queue-ref", "parent-count", "head", "base", "base-tree", "tree", "tested-tree", "changed-paths", "pr.json", "runs.json", "jobs.json", "artifacts.json" })
            {
                facts[name] = await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(settings.Facts, name), cancellationToken).ConfigureAwait(false);
            }

            reason = MergeGroupReuseDecision.Reason(facts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            console.WriteLine($"Cannot read reuse facts: {exception.Message}");
        }

        try
        {
            await fileSystem.File.AppendAllTextAsync(settings.Output,
                $"reuse={(reason == "all rules hold" ? "true" : "false")}\nreason={reason}\n", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            console.WriteLine($"Cannot write reuse decision: {exception.Message}");
        }

        console.WriteLine(reason);
        return 0;
    }

    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--facts <DIRECTORY>", true)]
        [Description("Directory containing raw merge-group reuse facts.")]
        public string Facts { get; init; } = string.Empty;

        [CommandOption("--output <FILE>", true)]
        [Description("GitHub Actions output file receiving the decision and reason.")]
        public string Output { get; init; } = string.Empty;
    }
}
