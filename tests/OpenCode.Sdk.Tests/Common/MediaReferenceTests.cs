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
    public async Task HashFromUri_Should_Refuse_Any_Other_Value()
    {
        string?[] values =
        [
            null, "", "MEDIA:" + Hash, "https:" + Hash,
            "media:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef",
            "media:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde",
            "media:" + Hash + "0", "media:" + Hash + "\n",
        ];
        foreach (var uri in values)
        {
            await Assert.That(MediaReference.HashFromUri(uri)).IsNull();
        }
    }
}
