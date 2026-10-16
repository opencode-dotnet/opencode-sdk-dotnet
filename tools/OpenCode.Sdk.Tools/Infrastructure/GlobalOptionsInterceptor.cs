using OpenCode.Sdk.Tools.Infrastructure.Logging;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools.Infrastructure;

/// <summary>Applies global command options before command execution.</summary>
internal sealed class GlobalOptionsInterceptor : ICommandInterceptor
{
    private readonly ToolLoggingOptions _loggingOptions;

    public GlobalOptionsInterceptor(ToolLoggingOptions loggingOptions)
    {
        ArgumentNullException.ThrowIfNull(loggingOptions);

        _loggingOptions = loggingOptions;
    }

    /// <inheritdoc/>
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings is not GlobalSettings globalSettings)
        {
            throw new InvalidOperationException("Every command settings type must derive from GlobalSettings.");
        }

        _loggingOptions.Apply(globalSettings.LogLevel, globalSettings.LogFile);
    }
}
