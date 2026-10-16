namespace OpenCode.Sdk.Tools.Tests.Support;

public sealed class FixtureLoaderTests
{
    [Test]
    public async Task Load_Should_Throw_With_Known_Names_When_Fixture_Missing()
    {
        var loader = new FixtureLoader();

        var exception = await Assert.That(() => loader.Load("no-such-fixture")).Throws<ArgumentException>();
        var message = exception?.Message ?? throw new InvalidOperationException("The assertion did not return the exception.");

        await Assert.That(message).Contains("no-such-fixture");
        await Assert.That(message).Contains("Known fixtures:");
    }
}
