using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.Tests.Ptys.Support;

/// <summary>
/// Observes one live PTY connection as complete newline-delimited records and server cursor
/// boundaries. Output is retained only to a fixed bound so a failed peer cannot grow the test
/// process without limit.
/// </summary>
internal sealed class PtyLiveTranscript
{
    private const int MaximumCharacters = 65_536;

    private const int MaximumFailureCharacters = 2_048;

    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(20);

    private readonly StringBuilder _currentRecord = new();
    private readonly List<string> _records = [];
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();
    private TerminalControl _control;
    private long? _cursor;
    private bool _skipLineFeed;

    public bool ContainsRecord(string record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            return _records.Contains(record, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Reads until the server's replay cursor and every named record have arrived. The cursor may
    /// precede a record produced just after attachment, which is the expected initial READY race.
    /// </summary>
    public async Task<long> ReadThroughReplayAsync(
        PtySession session,
        IReadOnlyList<string> requiredRecords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(requiredRecords);

        if (Reached(_cursor, requiredRecords))
        {
            return _cursor!.Value;
        }

        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        observation.CancelAfter(ObservationTimeout);
        try
        {
            await foreach (var frame in session.ReadAsync(observation.Token))
            {
                Observe(frame);
                if (Reached(_cursor, requiredRecords))
                {
                    return _cursor!.Value;
                }
            }
        }
        catch (OperationCanceledException) when (
            observation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw TimedOut("the replay cursor and records " + DescribeTargets(requiredRecords));
        }

        throw ClosedEarly("the replay cursor and records " + DescribeTargets(requiredRecords));
    }

    /// <summary>Reads until one exact complete record arrives.</summary>
    public async Task ReadUntilRecordAsync(
        PtySession session,
        string record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(record);

        if (ContainsRecord(record))
        {
            return;
        }

        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        observation.CancelAfter(ObservationTimeout);
        try
        {
            await foreach (var frame in session.ReadAsync(observation.Token))
            {
                Observe(frame);
                if (ContainsRecord(record))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            observation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw TimedOut("record '" + record + "'");
        }

        throw ClosedEarly("record '" + record + "'");
    }

    /// <summary>Reads until the server closes normally; abnormal closes still fail through the SDK.</summary>
    public async Task ReadToCompletionAsync(PtySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        await foreach (var frame in session.ReadAsync(cancellationToken))
        {
            Observe(frame);
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            return "cursor=" + (_cursor?.ToString(CultureInfo.InvariantCulture) ?? "<none>") +
                   " record-count=" + _records.Count.ToString(CultureInfo.InvariantCulture) +
                   " last-records=[" + string.Join(", ", _records.Skip(Math.Max(0, _records.Count - 8))
                       .Select(static record => "'" + (record.Length <= 128 ? record : record[..116] + " [truncated]") + "'")) +
                   "] text-characters=" + _text.Length.ToString(CultureInfo.InvariantCulture) + " text-tail=" + Excerpt();
        }
    }

    private static string DescribeTargets(IReadOnlyList<string> records) =>
        "[" + string.Join(", ", records.Select(static record => "'" + record + "'")) + "]";

    private bool Reached(long? cursor, IReadOnlyList<string> records) =>
        cursor is not null && records.All(ContainsRecord);

    private void Observe(PtyFrame frame)
    {
        lock (_gate)
        {
            if (frame is PtyCursorFrame cursor)
            {
                _cursor = cursor.Cursor;
                return;
            }

            if (frame is PtyOutputFrame output)
            {
                Append(output.Text);
            }
        }
    }

    private void Append(string text)
    {
        if (_text.Length + text.Length > MaximumCharacters)
        {
            throw new InvalidOperationException(
                "The live PTY transcript exceeded " + MaximumCharacters.ToString(CultureInfo.InvariantCulture) +
                " characters before reaching its target. " + Describe());
        }

        _ = _text.Append(text);
        foreach (var character in text)
        {
            if (ConsumeTerminalControl(character))
            {
                continue;
            }

            if (character is '\r')
            {
                CompleteRecord();
                _skipLineFeed = true;
                continue;
            }

            if (character is '\n')
            {
                if (!_skipLineFeed)
                {
                    CompleteRecord();
                }

                _skipLineFeed = false;
                continue;
            }

            _skipLineFeed = false;
            _ = _currentRecord.Append(character);
        }
    }

    /// <summary>
    /// Removes only syntactically delimited CSI and OSC controls from the record view while the
    /// raw transcript above retains every byte-derived character. Windows ConPTY emits these
    /// controls around otherwise plain child output, including immediately before READY.
    /// </summary>
    private bool ConsumeTerminalControl(char character)
    {
        if (_control is TerminalControl.None)
        {
            if (character is '\u001b')
            {
                _control = TerminalControl.Escape;
                return true;
            }

            return false;
        }

        if (_control is TerminalControl.Escape)
        {
            _control = character switch
            {
                '[' => TerminalControl.ControlSequence,
                ']' => TerminalControl.OperatingSystemCommand,
                _ => TerminalControl.None,
            };
            return true;
        }

        if (_control is TerminalControl.ControlSequence)
        {
            if (character is >= '@' and <= '~')
            {
                _control = TerminalControl.None;
                if (MovesToAnotherLine(character) && _currentRecord.Length > 0)
                {
                    CompleteRecord();
                }
            }

            return true;
        }

        if (_control is TerminalControl.OperatingSystemCommand)
        {
            if (character is '\u0007')
            {
                _control = TerminalControl.None;
            }
            else if (character is '\u001b')
            {
                _control = TerminalControl.OperatingSystemCommandEscape;
            }

            return true;
        }

        _control = character is '\\'
            ? TerminalControl.None
            : TerminalControl.OperatingSystemCommand;
        return true;
    }

    private static bool MovesToAnotherLine(char finalCharacter) =>
        finalCharacter is 'E' or 'F' or 'H' or 'd' or 'f';

    private void CompleteRecord()
    {
        _records.Add(_currentRecord.ToString());
        _ = _currentRecord.Clear();
    }

    private InvalidOperationException ClosedEarly(string target) =>
        new("The live PTY closed normally before it produced " + target + ". " + Describe());

    private TimeoutException TimedOut(string target) =>
        new("The live PTY did not produce " + target + " within " +
            ObservationTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s. " + Describe());

    private string Excerpt()
    {
        var text = _text.ToString();
        var start = Math.Max(0, text.Length - MaximumFailureCharacters);
        var escaped = new StringBuilder(text.Length - start);
        foreach (var character in text[start..])
        {
            _ = character switch
            {
                '\r' => escaped.Append("\\r"),
                '\n' => escaped.Append("\\n"),
                _ => escaped.Append(character),
            };
        }

        return escaped.ToString();
    }

    private enum TerminalControl
    {
        None,
        Escape,
        ControlSequence,
        OperatingSystemCommand,
        OperatingSystemCommandEscape,
    }
}
