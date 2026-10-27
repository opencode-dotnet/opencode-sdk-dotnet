using System.Globalization;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>When each step of a child-tree handshake was observed done, measured from its start, for a timeout to report.</summary>
internal sealed class HandshakeTimeline(TimeProvider time)
{
    private readonly long _start = time.GetTimestamp();
    private readonly List<string> _completed = [];

    public void Completed(string step) => _completed.Add(step + " at " + Elapsed());

    public string Describe(int? unreadRootBytes) =>
        "Steps observed done before the timeout: " + (_completed.Count is 0 ? "none" : string.Join(", ", _completed))
        + "; the timeout was observed " + Elapsed() + " after the handshake began; "
        + (unreadRootBytes is { } bytes ? bytes.ToString(CultureInfo.InvariantCulture) + " bytes waiting unread on the root socket." : "no root socket yet.");

    private string Elapsed() =>
        time.GetElapsedTime(_start).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s";
}
