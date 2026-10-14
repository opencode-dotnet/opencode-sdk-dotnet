
namespace OpenCode.Sdk.Tests.Sessions;

public sealed class SessionParentFilterTests
{
    [Test]
    public async Task RootOnly_Should_Carry_The_Literal_Null_Wire_Value()
    {
        await Assert.That(SessionParentFilter.RootOnly.WireValue).IsEqualTo("null");
    }

    [Test]
    public async Task RootOnly_Should_Be_A_Singleton()
    {
        await Assert.That(ReferenceEquals(SessionParentFilter.RootOnly, SessionParentFilter.RootOnly)).IsTrue();
    }

    [Test]
    public async Task Of_Should_Carry_The_Identifier()
    {
        await Assert.That(SessionParentFilter.Of("ses_123").WireValue).IsEqualTo("ses_123");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Of_Should_Refuse_A_Blank_Identifier(string? sessionId)
    {
        _ = Assert.Throws<ArgumentException>(() => _ = SessionParentFilter.Of(sessionId!));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Of_Should_Refuse_The_Literal_Null_Spelling()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = SessionParentFilter.Of("null"));

        await Assert.That(exception.Message).Contains("RootOnly");
    }
}
