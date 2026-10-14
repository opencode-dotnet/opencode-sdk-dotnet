using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Server;

public sealed class ServerInfoResponseAdapterTests
{
    [Test]
    public async Task Classify_Should_Read_The_Pinned_Status_Table()
    {
        var adapter = ServerInfoResponseAdapter.Instance;

        await Assert.That(adapter.Classify(200)).IsEqualTo(StatusVerdict.Success);
        await Assert.That(adapter.Classify(204)).IsEqualTo(StatusVerdict.UndeclaredSuccess);
        await Assert.That(adapter.Classify(299)).IsEqualTo(StatusVerdict.UndeclaredSuccess);
        await Assert.That(adapter.Classify(400)).IsEqualTo(StatusVerdict.DeclaredError);
        await Assert.That(adapter.Classify(401)).IsEqualTo(StatusVerdict.DeclaredError);
        await Assert.That(adapter.Classify(404)).IsEqualTo(StatusVerdict.UndeclaredError);
        await Assert.That(adapter.Classify(500)).IsEqualTo(StatusVerdict.UndeclaredError);
        await Assert.That(adapter.Classify(199)).IsEqualTo(StatusVerdict.UndeclaredError);
    }
}
