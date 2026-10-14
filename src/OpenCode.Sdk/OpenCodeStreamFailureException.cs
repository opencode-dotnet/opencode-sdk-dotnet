using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>Represents a schema-valid failure reported after an opencode event stream opened.</summary>
public sealed class OpenCodeStreamFailureException : OpenCodeTransportException
{
    private static readonly IReadOnlyList<IOpenCodeStreamFailureCause> EmptyCauses =
        Array.AsReadOnly(Array.Empty<IOpenCodeStreamFailureCause>());

    /// <summary>Initializes an exception with no causes.</summary>
    public OpenCodeStreamFailureException()
    {
        Causes = EmptyCauses;
    }

    /// <summary>Initializes an exception with a message and no causes.</summary>
    /// <param name="message">The failure description.</param>
    public OpenCodeStreamFailureException(string message)
        : base(message)
    {
        Causes = EmptyCauses;
    }

    /// <summary>Initializes an exception with a message, underlying failure, and no causes.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OpenCodeStreamFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
        Causes = EmptyCauses;
    }

    /// <summary>Initializes an exception with the causes reported by the stream.</summary>
    /// <param name="causes">The typed reasons reported by the reserved failure frame.</param>
    public OpenCodeStreamFailureException(IReadOnlyList<IOpenCodeStreamFailureCause> causes)
        : base("The opencode event stream reported a failure after it opened.")
    {
        ArgumentNullException.ThrowIfNull(causes);
        Causes = causes;
    }

    /// <summary>Gets the typed reasons reported by the stream.</summary>
    public IReadOnlyList<IOpenCodeStreamFailureCause> Causes { get; }
}
