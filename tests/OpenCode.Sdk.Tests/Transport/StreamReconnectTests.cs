using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class StreamReconnectTests
{
    /// <summary>The internal connection predicate refuses a named frame even when its payload announces a connection.</summary>
    [Test]
    public async Task IsConnected_Should_Refuse_A_Named_Connected_Frame()
    {
        var frame = TestFrames.FromText("notice", ScriptedEventServer.Connected);

        await Assert.That(StreamReconnect.IsConnected(frame)).IsFalse();
    }
}
