using OpenCode.Sdk.Tools.Generator.Emission;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class GeneratedSourceTests
{
    [Test]
    public async Task GeneratedSource_Should_Keep_The_Defensive_Init_Copy()
    {
        byte[] callerBytes = [0x41, 0xC3, 0xA9, 0x0A];
        var source = new GeneratedSource
        {
            RelativePath = "Example.cs",
            Utf8Source = callerBytes,
        };

        callerBytes[0] = 0x5A;

        await Assert.That(source.Utf8Source.ToArray()).IsEquivalentTo(new byte[] { 0x41, 0xC3, 0xA9, 0x0A }, CollectionOrdering.Matching);
    }
}
