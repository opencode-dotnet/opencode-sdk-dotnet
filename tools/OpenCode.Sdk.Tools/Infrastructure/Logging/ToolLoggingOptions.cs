using Microsoft.Extensions.Logging;

namespace OpenCode.Sdk.Tools.Infrastructure.Logging;

/// <summary>Holds mutable logging options for one tool invocation.</summary>
internal sealed class ToolLoggingOptions
{
    /// <summary>Gets the minimum enabled logging level.</summary>
    public LogLevel MinimumLevel { get; private set; } = LogLevel.Warning;

    /// <summary>Gets the optional path receiving file logs.</summary>
    public string? LogFile { get; private set; }

    /// <summary>Applies logging options parsed from command settings.</summary>
    public void Apply(LogLevel minimumLevel, string? logFile)
    {
        if (!Enum.IsDefined(minimumLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel), minimumLevel, "The log level is not defined.");
        }

        if (logFile is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logFile);
        }

        MinimumLevel = minimumLevel;
        LogFile = logFile;
    }
}
