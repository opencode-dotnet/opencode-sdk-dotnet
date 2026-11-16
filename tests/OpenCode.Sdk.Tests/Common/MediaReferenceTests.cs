namespace OpenCode.Sdk.Tests.Common;

public sealed class MediaReferenceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public async Task HashFromUri_Should_Return_The_Hash_Of_A_Valid_Ref()
    {
        await Assert.That(MediaReference.HashFromUri("media:" + Hash)).IsEqualTo(Hash);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("MEDIA:" + Hash)]
    [Arguments("https:" + Hash)]
    [Arguments("media:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [Arguments("media:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [Arguments("media:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [Arguments("media:" + Hash + "0")]
    [Arguments("media:" + Hash + "\n")]
    public async Task HashFromUri_Should_Refuse_Any_Other_Value(string? value)
    {
        await Assert.That(MediaReference.HashFromUri(value)).IsNull();
    }
}
