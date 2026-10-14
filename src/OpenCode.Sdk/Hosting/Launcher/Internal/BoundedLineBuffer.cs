using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Retains the newest lines of one output stream under two bounds — a line count and a total
/// content length — dropping the oldest lines to satisfy both. An oversized single line keeps
/// its suffix. Any discard, an empty line included, marks the buffer truncated for good: a
/// reader is told that evidence was lost even when the lost content was zero characters.
/// Not thread-safe; the owner serializes access.
/// </summary>
internal sealed class BoundedLineBuffer
{
    private readonly Queue<string> _lines = new();
    private readonly int _maximumLines;
    private readonly int _maximumCharacters;
    private int _characters;

    public BoundedLineBuffer(int maximumLines, int maximumCharacters)
    {
        Debug.Assert(maximumLines > 0, "A buffer retains at least one line.");
        Debug.Assert(maximumCharacters > 0, "A buffer retains at least one character.");
        _maximumLines = maximumLines;
        _maximumCharacters = maximumCharacters;
    }

    /// <summary>Gets whether any line or part of a line has ever been discarded.</summary>
    public bool Truncated { get; private set; }

    public void Append(string line)
    {
        if (line.Length > _maximumCharacters)
        {
            line = line[^_maximumCharacters..];
            Truncated = true;
        }

        _lines.Enqueue(line);
        _characters += line.Length;
        while (_lines.Count > _maximumLines || _characters > _maximumCharacters)
        {
            _characters -= _lines.Dequeue().Length;
            Truncated = true;
        }
    }

    /// <summary>Copies the retained lines, oldest first, into a list later appends cannot touch.</summary>
    public IReadOnlyList<string> Snapshot() => [.. _lines];
}
