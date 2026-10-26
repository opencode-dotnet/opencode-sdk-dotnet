namespace OpenCode.Sdk.Tests.Support;

internal sealed record RecordedRequest
{
    public required Uri? RequestUri { get; init; }

    public required HttpMethod Method { get; init; }

    /// <summary>Gets every request header, values joined with commas.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public string? Authorization { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Gets the User-Agent as the handler writes it, from the unvalidated view; null on .NET Framework.</summary>
    public string? RawUserAgent { get; init; }

    public string? ContentType { get; init; }

    public string? Body { get; init; }

    /// <summary>Gets the raw request body, or null when the request carried none.</summary>
    public IReadOnlyList<byte>? BodyBytes { get; init; }

    /// <summary>Gets the Content-Length the content declared before it was buffered; null when it declared none.</summary>
    public long? ContentLength { get; init; }
}
