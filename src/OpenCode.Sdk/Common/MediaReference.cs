namespace OpenCode.Sdk;

/// <summary>Reads hashes from experimental media references returned by opted-in message reads.</summary>
/// <remarks>
/// <see cref="OpenCodeClientOptions.MediaReferences"/> requests the capability on session.message.list
/// (each cursor page included), session.message.get and session.inbox.list. The session location's
/// experimental.media_blob setting decides whether stored media stays a ref.
/// Use client.Experimental.GetMediaAsync to fetch the referenced bytes.
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public static class MediaReference
{
    /// <summary>Returns the 64 lowercase-hex SHA-256 from a media:&lt;hash&gt; reference, or null for any other value.</summary>
    /// <param name="uri">A media reference, or null.</param>
    /// <returns>The hash to pass to client.Experimental.GetMediaAsync, or null when the value is not a reference.</returns>
    [System.Diagnostics.CodeAnalysis.Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
    public static string? HashFromUri(string? uri)
    {
        if (uri is null || uri.Length != 70 || !uri.StartsWith("media:", StringComparison.Ordinal))
        {
            return null;
        }

        for (var index = 6; index < uri.Length; index++)
        {
            if (uri[index] is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return null;
            }
        }

        return uri[6..];
    }
}
