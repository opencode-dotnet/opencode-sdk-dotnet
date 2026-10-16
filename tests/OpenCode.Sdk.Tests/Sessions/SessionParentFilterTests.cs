
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

    [Test]
    public async Task Of_Should_Use_The_Wire_Value_For_Equality_And_Hash_Codes()
    {
        var left = SessionParentFilter.Of("a");
        var right = SessionParentFilter.Of("a");
        var different = SessionParentFilter.Of("b");
        var differentCase = SessionParentFilter.Of("A");

        await Assert.That(left).IsNotSameReferenceAs(right);
        await Assert.That(left.Equals(right)).IsTrue();
        await Assert.That(left.Equals((object)right)).IsTrue();
        await Assert.That(left == right).IsTrue();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
        await Assert.That(left.Equals(different)).IsFalse();
        await Assert.That(left != different).IsTrue();
        await Assert.That(left.Equals(differentCase)).IsFalse();
        await Assert.That(left != differentCase).IsTrue();
        await Assert.That(left.Equals(SessionParentFilter.RootOnly)).IsFalse();
        await Assert.That(left != SessionParentFilter.RootOnly).IsTrue();
    }

    [Test]
    public async Task ToString_Should_Describe_Root_Only()
    {
        await Assert.That(SessionParentFilter.RootOnly.ToString()).IsEqualTo("SessionParentFilter { Kind = RootOnly }");
    }

    [Test]
    [Arguments("a", "SessionParentFilter { Kind = Of, SessionId = a }")]
    [Arguments("{a}\"\\", "SessionParentFilter { Kind = Of, SessionId = {a}\"\\ }")]
    public async Task ToString_Should_Describe_The_Parent_Identifier(string sessionId, string expected)
    {
        await Assert.That(SessionParentFilter.Of(sessionId).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task SessionListRequest_Should_Compare_Equal_When_Parent_Filters_Are_Equal()
    {
        var left = new SessionListRequest { ParentId = SessionParentFilter.Of("a") };
        var right = new SessionListRequest { ParentId = SessionParentFilter.Of("a") };
        var different = new SessionListRequest { ParentId = SessionParentFilter.Of("b") };

        await Assert.That(left.Equals(right)).IsTrue();
        await Assert.That(left == right).IsTrue();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
        await Assert.That(left.Equals(different)).IsFalse();
    }
}
