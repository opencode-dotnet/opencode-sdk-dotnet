namespace OpenCode.Sdk.Tests.Common.Options;

public sealed class LocationSelectorTests
{
    [Test]
    public async Task Members_Should_Stay_Null_When_Unset()
    {
        var selector = new LocationSelector();

        await Assert.That(selector.Directory).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Directory_Should_Refuse_A_Blank_Value(string value)
    {
        _ = await Assert.That(() => _ = new LocationSelector { Directory = value })
            .Throws<ArgumentException>();
    }

}
