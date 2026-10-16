using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>Represents one or more failures that prevent generator binding.</summary>
public sealed class BindingException : ToolRefusalException
{
    private static readonly IReadOnlyList<BindingError> NoErrors = Array.AsReadOnly(Array.Empty<BindingError>());

    /// <summary>Initializes an exception without binding errors.</summary>
    public BindingException()
    {
        Errors = NoErrors;
    }

    /// <summary>Initializes an exception with a caller-supplied message.</summary>
    /// <param name="message">The message that describes the exception.</param>
    public BindingException(string message)
        : base(message ?? throw new ArgumentNullException(nameof(message)))
    {
        Errors = NoErrors;
    }

    /// <summary>Initializes an exception with a caller-supplied message and inner exception.</summary>
    /// <param name="message">The message that describes the exception.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public BindingException(string message, Exception innerException)
        : base(message ?? throw new ArgumentNullException(nameof(message)), innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
        Errors = NoErrors;
    }

    /// <summary>Initializes an exception for the supplied binding errors.</summary>
    /// <param name="errors">The failures that prevent binding.</param>
    public BindingException(IReadOnlyList<BindingError> errors)
        : this(CopyErrors(errors))
    {
    }

    private BindingException(ReadOnlyCollection<BindingError> errors)
        : base(CreateMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Gets the failures that prevent binding.</summary>
    public IReadOnlyList<BindingError> Errors { get; }

    private static ReadOnlyCollection<BindingError> CopyErrors(IReadOnlyList<BindingError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return Array.AsReadOnly([.. errors]);
    }

    private static string CreateMessage(ReadOnlyCollection<BindingError> errors)
    {
        var message = new StringBuilder("Binding failed with ")
            .Append(errors.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" error(s):");
        foreach (var error in errors)
        {
            message
                .Append('\n')
                .Append("- ")
                .Append(error.Category)
                .Append(" [")
                .Append(error.Subject)
                .Append("]: ")
                .Append(error.Problem);
        }

        return message.ToString();
    }
}
