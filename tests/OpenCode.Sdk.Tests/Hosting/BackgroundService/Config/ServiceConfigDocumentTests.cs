using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigDocumentTests
{
    [Test]
    public async Task TryReadDocument_Should_Retain_Every_Declared_Value()
    {
        var document = ServiceConfigReader.TryReadDocument(Encoding.UTF8.GetBytes(new ServiceConfigScenario().Initial("all-members")!));

        await Assert.That(document).IsNotNull();
        await Assert.That(document.Disabled).IsTrue();
        await Assert.That(document.RemoteAccepted).IsTrue();
        await Assert.That(document.RemoteRoute).IsEqualTo("stored-route");
        await Assert.That(document.LegacyRemote).IsNull();
        await Assert.That(document.Hostname).IsEqualTo("127.0.0.2");
        await Assert.That(document.Port).IsEqualTo(5123);
        await Assert.That(document.Password).IsEqualTo("stored-password");
        await Assert.That(document.CorsOrigins).IsEquivalentTo(["https://b.example", "https://a.example"], CollectionOrdering.Matching);
        await Assert.That(document.Environment).IsEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["B"] = "two",
            ["10"] = "ten",
            ["XDG_STATE_HOME"] = "saved-state",
        }, CollectionOrdering.Any);
    }
}
