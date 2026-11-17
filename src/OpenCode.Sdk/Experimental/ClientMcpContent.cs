using System.Diagnostics.CodeAnalysis;

namespace OpenCode.Sdk;

/// <summary>One text or base64-image part; unrelated arm fields are omitted on the wire.</summary>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public sealed class ClientMcpContent
{
    private ClientMcpContent(string type, string? text, string? data, string? mimeType)
    {
        Type = type;
        Text = text;
        Data = data;
        MimeType = mimeType;
    }

    /// <summary>Gets text or image.</summary>
    public string Type { get; }
    /// <summary>Gets text for the text arm only.</summary>
    public string? Text { get; }
    /// <summary>Gets unchanged base64 data for the image arm only.</summary>
    public string? Data { get; }
    /// <summary>Gets the unchanged image MIME type.</summary>
    public string? MimeType { get; }

    /// <summary>Creates a text part.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text part.</returns>
    public static ClientMcpContent FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ClientMcpContent("text", text, null, null);
    }

    /// <summary>Creates an image part without decoding or uploading it.</summary>
    /// <param name="data">Base64 image data.</param>
    /// <param name="mimeType">The image MIME type.</param>
    /// <returns>The image part.</returns>
    public static ClientMcpContent FromImage(string data, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(mimeType);
        return new ClientMcpContent("image", null, data, mimeType);
    }
}
