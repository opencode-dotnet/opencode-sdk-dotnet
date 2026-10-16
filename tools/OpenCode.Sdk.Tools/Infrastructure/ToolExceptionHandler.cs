using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Infrastructure;

/// <summary>Maps invocation failures to diagnostics and exit codes without changing framework error rendering.</summary>
internal sealed class ToolExceptionHandler(IAnsiConsole console)
{
    public int Handle(Exception exception, int cancellationExitCode, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return cancellationExitCode;
        }

        if (exception is ToolRefusalException)
        {
            console.MarkupLineUnwrapped($"[red]Refused:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }

        if (exception is CommandAppException { Pretty: not null } pretty)
        {
            console.Write(pretty.Pretty);
        }
        else
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
            if (exception.InnerException is CommandAppException { Pretty: not null } inner)
            {
                console.Write(inner.Pretty);
            }
        }

        return -1;
    }
}
