namespace OpenCode.Sdk;

/// <summary>
/// Selects the directory a request addresses. A declared query selector uses
/// <c>location[directory]</c>; the client default and per-call request options use the
/// percent-encoded <c>x-opencode-directory</c> header. An unset per-call directory inherits
/// the ambient value; an explicit directory overrides it.
/// </summary>
public sealed record LocationSelector
{
    private readonly string? _directory;

    /// <summary>Gets the directory the request addresses; a blank value is refused.</summary>
    public string? Directory
    {
        get => _directory;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("The location directory cannot be empty or whitespace; leave it null when unset.", nameof(value));
            }

            _directory = value;
        }
    }
}
