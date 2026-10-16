using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace OpenCode.Sdk.Tools.Infrastructure.Logging;

/// <summary>Provides optional MEL file logging through the configured filesystem seam.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider, ILogger
{
    private readonly IFileSystem _fileSystem;
    private readonly ToolLoggingOptions _options;

    public FileLoggerProvider(IFileSystem fileSystem, ToolLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(options);

        _fileSystem = fileSystem;
        _options = options;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);

        if (_options.LogFile is not { } logFile)
        {
            return this;
        }

        var directory = _fileSystem.Path.GetDirectoryName(logFile);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        _fileSystem.File.AppendAllText(logFile, string.Empty);

        return this;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => _options.LogFile is not null && logLevel >= _options.MinimumLevel;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var logFile = _options.LogFile;
        if (logFile is null || !IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception is not null)
        {
            message = $"{message} {exception}";
        }

        _fileSystem.File.AppendAllText(logFile, $"{logLevel}: {message}\n");
    }

    /// <inheritdoc/>
    public void Dispose() => GC.SuppressFinalize(this);
}
