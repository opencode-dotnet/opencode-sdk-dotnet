using System.Text;

namespace OpenCode.Sdk;

/// <summary>
/// Terminal output from the pseudo-terminal. The server chunks its retained replay at 64Ki UTF-16
/// code units, so a chunk boundary can split a surrogate pair; the text is therefore decoded with
/// replacement rather than refused, and a caller that concatenates consecutive frames sees the
/// stream the terminal produced.
/// </summary>
/// <remarks>A frame received by the SDK keeps its UTF-8 until <see cref="Text"/> is first read.</remarks>
public sealed class PtyOutputFrame : PtyFrame
{
    /// <summary>
    /// The received UTF-8 until <see cref="Text"/> is first read, then the decoded string: one field, so a racing first
    /// read sees either the bytes, and decodes them too, or the published text, never neither. The first decode to
    /// publish wins.
    /// </summary>
    private object _content;

    /// <summary>
    /// Initializes an output frame. Public so a consumer substituting <see cref="PtySession"/>
    /// can script the frames its override yields.
    /// </summary>
    /// <param name="text">The output the frame carries.</param>
    public PtyOutputFrame(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _content = text;
    }

    internal PtyOutputFrame(ReadOnlySpan<byte> utf8) => _content = utf8.ToArray();

    /// <summary>Gets the decoded output this frame carries.</summary>
    /// <remarks>
    /// A frame the SDK received decodes its UTF-8 on the first read; every read returns the same string instance.
    /// </remarks>
    public string Text
    {
        get
        {
            var content = Volatile.Read(ref _content);
            if (content is string text)
            {
                return text;
            }

            // The first decode to publish wins, so every read after it returns that one instance.
            text = Encoding.UTF8.GetString((byte[])content);
            return Interlocked.CompareExchange(ref _content, text, content) as string ?? text;
        }
    }
}
