namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// One wire-shaped input a benchmark operation consumes, carrying the exact byte counts the
/// report places beside the measured allocation: the complete body as the wire delivers it
/// and how many logical items (payloads or frames) that body carries. The report columns read
/// these so payload size is visible in every ordinary summary, not only in prose.
/// </summary>
public sealed record WireFixture
{
    public WireFixture(string name, byte[] body, int items, int payloadBytesPerItem, string? charset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(items);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytesPerItem);

        Name = name;
        Bytes = body;
        Items = items;
        PayloadBytesPerItem = payloadBytesPerItem;
        Charset = charset;
    }

    /// <summary>Gets the short label the summary table prints for this input.</summary>
    public string Name { get; }

    /// <summary>Gets the complete body exactly as the wire carries it: envelope, framing, and payload.</summary>
    public ReadOnlyMemory<byte> Body => Bytes;

    /// <summary>Gets the number of bytes one benchmark operation consumes from the wire.</summary>
    public int WireBytes => Bytes.Length;

    /// <summary>Gets the body buffer a benchmark hands to streams, handlers, and the serializer.</summary>
    internal byte[] Bytes { get; }

    /// <summary>Gets how many logical items (payloads or frames) one operation consumes; zero for a bodiless response.</summary>
    public int Items { get; }

    /// <summary>Gets the JSON payload bytes per item, excluding envelope and framing; an average for a mixed body.</summary>
    public int PayloadBytesPerItem { get; }

    /// <summary>Gets the Content-Type charset the fixture declares, or <see langword="null"/> when the wire declares none.</summary>
    public string? Charset { get; }

    /// <summary>Gets the resolved text for a terminal-write argument; absent for other fixtures.</summary>
    internal string? InputText { get; init; }

    /// <summary>Gets the resolved caller bytes for a persistent-terminal write, excluding its wire header.</summary>
    internal ReadOnlyMemory<byte> InputBytes { get; init; }

    public override string ToString() => Name;
}
