using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace OpenCode.Sdk.Tools.Infrastructure.Logging;

/// <summary>Converts the tool's log-level vocabulary to MEL levels.</summary>
internal sealed class ToolLogLevelConverter : TypeConverter
{
    /// <inheritdoc/>
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc/>
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
        {
            return text.Trim().ToLowerInvariant() switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Information,
                "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                "none" => LogLevel.None,
                _ => throw new FormatException("Log level must be one of: trace, debug, info, warning, error, none."),
            };
        }

        return base.ConvertFrom(context, culture, value);
    }
}
