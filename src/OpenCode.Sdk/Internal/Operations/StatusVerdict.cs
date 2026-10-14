namespace OpenCode.Sdk.Internal;

/// <summary>
/// What one HTTP status means under an operation's pinned contract. The generated adapter's
/// <c>Classify</c> is the single authority producing these; the planes and the materializer
/// switch on the verdict and never re-derive status meaning. A redirect never reaches a
/// verdict: 3xx is transport's protocol-invariant refusal.
/// </summary>
internal enum StatusVerdict
{
    /// <summary>The declared success status whose body materializes the payload.</summary>
    Success,

    /// <summary>The declared success status that carries no payload; an unexpected body is drained and ignored.</summary>
    NoContentSuccess,

    /// <summary>An error status the operation's pinned error map declares.</summary>
    DeclaredError,

    /// <summary>An error status outside the declared map, read tolerantly.</summary>
    UndeclaredError,

    /// <summary>A success status the contract does not declare: a protocol failure.</summary>
    UndeclaredSuccess,
}
