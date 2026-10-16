using System.ComponentModel;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Infrastructure.Logging;
using Spectre.Console.Cli;

namespace OpenCode.Sdk.Tools;

/// <summary>Defines options shared by every tool command.</summary>
internal abstract class GlobalSettings : CommandSettings
{
    /// <summary>Gets or sets the minimum logging level for the invocation.</summary>
    [CommandOption("--log-level <LEVEL>")]
    [Description("Minimum log level: trace, debug, info, warning, error, none.")]
    [DefaultValue(LogLevel.Warning)]
    [TypeConverter(typeof(ToolLogLevelConverter))]
    public LogLevel LogLevel { get; init; } = LogLevel.Warning;

    /// <summary>Gets or sets the optional log-file path.</summary>
    [CommandOption("--log-file <PATH>")]
    [Description("Optional file path for logging output.")]
    public string? LogFile { get; init; }
}
