using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting;

public sealed class RandomCredentialTests
{
    [Test]
    public async Task Route_Should_Encode_Exactly_Eight_Scripted_Bytes()
    {
        using var random = new ScriptedRandomNumberGenerator();
        var credential = new RandomCredential(random);
        await Assert.That(credential.Route()).IsEqualTo("0011223344556677");
        await Assert.That(credential.Route()).IsEqualTo("8899aabbccddeeff");
        await Assert.That(random.Requests).IsEquivalentTo([8, 8], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Password_Should_Encode_Exactly_ThirtyTwo_Scripted_Bytes_Without_Padding()
    {
        using var random = new ScriptedRandomNumberGenerator();
        var credential = new RandomCredential(random);
        await Assert.That(credential.Password()).IsEqualTo("ABEiM0RVZneImaq7zN3u_xAhMkNUZXaHmKm6y9zt_g8");
        await Assert.That(credential.Password()).IsEqualTo("IDFCU2R1hpeoucrb7P0OHzBBUmN0hZanuMna6_wNHi8");
        await Assert.That(credential.Password()).IsEqualTo("QFFic4SVprfI2er7DB0uP1BhcoOUpbbH2On6CxwtPk8");
        await Assert.That(credential.Password()).IsEqualTo("YHGCk6S1xtfo-QobLD1OX3CBkqO0xdbn-AkaKzxNXm8");
        await Assert.That(random.Requests).IsEquivalentTo([32, 32, 32, 32], CollectionOrdering.Matching);
    }
}
