using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace OpenCode.Sdk.Tools.Infrastructure.Logging;

/// <summary>Provides MEL loggers that render through the configured Spectre console.</summary>
internal sealed class SpectreConsoleLoggerProvider : ILoggerProvider, ILogger
{
    private readonly IAnsiConsole _console;
    private readonly ToolLoggingOptions _options;

    public SpectreConsoleLoggerProvider(IAnsiConsole console, ToolLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(options);

        _console = console;
        _options = options;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return this;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _options.MinimumLevel;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception is not null)
        {
            message = $"{message} {exception}";
        }

        _console.WriteLine($"{logLevel}: {message}");
    }

    /// <inheritdoc/>
    public void Dispose() => GC.SuppressFinalize(this);
}
