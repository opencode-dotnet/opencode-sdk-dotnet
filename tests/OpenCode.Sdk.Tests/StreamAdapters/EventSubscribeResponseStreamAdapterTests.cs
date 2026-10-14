using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.StreamAdapters;

public sealed class EventSubscribeResponseStreamAdapterTests
{
    [Test]
    public async Task Classify_Should_Read_The_Stream_Status_Table()
    {
        var adapter = EventSubscribeResponseStreamAdapter.Instance;

        await Assert.That(adapter.Classify(200)).IsEqualTo(StatusVerdict.Success);
        await Assert.That(adapter.Classify(201)).IsEqualTo(StatusVerdict.UndeclaredSuccess);
        await Assert.That(adapter.Classify(400)).IsEqualTo(StatusVerdict.DeclaredError);
        await Assert.That(adapter.Classify(401)).IsEqualTo(StatusVerdict.DeclaredError);
        await Assert.That(adapter.Classify(500)).IsEqualTo(StatusVerdict.UndeclaredError);
    }
}
