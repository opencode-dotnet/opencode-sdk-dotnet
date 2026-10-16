using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents one or more failures that prevent OpenAPI ingestion.</summary>
public sealed class IngestionException : ToolRefusalException
{
    private static readonly IReadOnlyList<IngestionError> NoErrors = Array.AsReadOnly(Array.Empty<IngestionError>());

    /// <summary>Initializes an exception without ingestion errors.</summary>
    public IngestionException()
    {
        Errors = NoErrors;
    }

    /// <summary>Initializes an exception with a caller-supplied message.</summary>
    /// <param name="message">The message that describes the exception.</param>
    public IngestionException(string message)
        : base(message ?? throw new ArgumentNullException(nameof(message)))
    {
        Errors = NoErrors;
    }

    /// <summary>Initializes an exception with a caller-supplied message and inner exception.</summary>
    /// <param name="message">The message that describes the exception.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public IngestionException(string message, Exception innerException)
        : base(message ?? throw new ArgumentNullException(nameof(message)), innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
        Errors = NoErrors;
    }

    /// <summary>Initializes an exception for the supplied ingestion errors.</summary>
    /// <param name="errors">The failures that prevent ingestion.</param>
    public IngestionException(IReadOnlyList<IngestionError> errors)
        : this(CopyErrors(errors))
    {
    }

    private IngestionException(ReadOnlyCollection<IngestionError> errors)
        : base(CreateMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Gets the failures that prevent ingestion.</summary>
    public IReadOnlyList<IngestionError> Errors { get; }

    private static ReadOnlyCollection<IngestionError> CopyErrors(IReadOnlyList<IngestionError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return Array.AsReadOnly([.. errors]);
    }

    private static string CreateMessage(ReadOnlyCollection<IngestionError> errors)
    {
        var message = new StringBuilder("Ingestion failed with ")
            .Append(errors.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" error(s):");

        foreach (var error in errors)
        {
            message
                .Append('\n')
                .Append("- ")
                .Append(error.Location)
                .Append(": ")
                .Append(error.Problem);
        }

        return message.ToString();
    }
}
