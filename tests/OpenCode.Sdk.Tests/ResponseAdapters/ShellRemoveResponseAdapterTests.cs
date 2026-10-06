using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.ResponseAdapters;

namespace OpenCode.Sdk.Tests.ResponseAdapters;

public sealed class ShellRemoveResponseAdapterTests
{
    [Test]
    public async Task Classify_Should_Read_The_No_Content_Status_Table()
    {
        var adapter = ShellRemoveResponseAdapter.Instance;

        await Assert.That(adapter.Classify(204)).IsEqualTo(StatusVerdict.NoContentSuccess);
        await Assert.That(adapter.Classify(200)).IsEqualTo(StatusVerdict.UndeclaredSuccess);
        await Assert.That(adapter.Classify(400)).IsEqualTo(StatusVerdict.DeclaredError);
        await Assert.That(adapter.Classify(401)).IsEqualTo(StatusVerdict.DeclaredError);
        await Assert.That(adapter.Classify(404)).IsEqualTo(StatusVerdict.DeclaredError);
        await Assert.That(adapter.Classify(500)).IsEqualTo(StatusVerdict.UndeclaredError);
    }
}
