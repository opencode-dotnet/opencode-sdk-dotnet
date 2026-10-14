using System.Net.Http.Headers;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class ResponseEncodingPolicyTests
{
    [Test]
    public async Task Decode_Should_Slice_A_Utf8_Bom_From_The_Byte_Path()
    {
        var payload = Encoding.UTF8.GetBytes(WireBodyData.InfoOk);
        var body = Encoding.UTF8.GetPreamble().Concat(payload).ToArray();

        var decoded = ResponseEncodingPolicy.Decode(body, charset: null);

        await Assert.That(decoded.DecodedBody).IsNull();
        await Assert.That(decoded.Utf8Body.Span.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Decode_Should_Use_A_Declared_Non_Utf8_Encoding_Without_A_Bom()
    {
        var body = Encoding.Unicode.GetBytes(WireBodyData.InfoOk);

        var decoded = ResponseEncodingPolicy.Decode(body, "utf-16");

        await Assert.That(decoded.DecodedBody).IsEqualTo(WireBodyData.InfoOk);
        await Assert.That(decoded.Utf8Body.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Decode_Should_Prefer_A_Utf32_Bom_Over_Its_Utf16_Prefix()
    {
        var body = Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(WireBodyData.InfoOk)).ToArray();

        var decoded = ResponseEncodingPolicy.Decode(body, charset: null);

        await Assert.That(decoded.DecodedBody).IsEqualTo(WireBodyData.InfoOk);
    }

    [Test]
    public async Task Decode_Should_Return_An_Empty_Body_Before_Validating_The_Charset()
    {
        var decoded = ResponseEncodingPolicy.Decode([], "not-an-encoding");

        await Assert.That(decoded.GetDecodedBody()).IsEmpty();
    }

    [Test]
    public async Task Decode_Should_Accept_A_Quoted_Utf8_Charset()
    {
        var body = Encoding.UTF8.GetBytes(WireBodyData.InfoOk);

        var decoded = ResponseEncodingPolicy.Decode(body, "\"utf-8\"");

        await Assert.That(decoded.DecodedBody).IsNull();
        await Assert.That(decoded.GetDecodedBody()).IsEqualTo(WireBodyData.InfoOk);
    }

    [Test]
    public async Task Decode_Should_Return_Empty_For_A_Bom_Only_Body()
    {
        var decoded = ResponseEncodingPolicy.Decode(Encoding.UTF8.GetPreamble(), charset: null);

        await Assert.That(decoded.GetDecodedBody()).IsEmpty();
    }

    [Test]
    public async Task Decode_Should_Bound_Itself_To_The_Count_Over_A_Pooled_Array()
    {
        var payload = Encoding.UTF8.GetBytes(WireBodyData.InfoOk);
        var pooled = new byte[payload.Length + 32];
        payload.CopyTo(pooled, 0);
        pooled.AsSpan(payload.Length).Fill(0xFF);

        var decoded = ResponseEncodingPolicy.Decode(pooled, payload.Length, charset: null);

        await Assert.That(decoded.Utf8Body.Span.SequenceEqual(payload)).IsTrue();
    }

    public static IEnumerable<Func<(byte[] Body, string? Charset)>> ParityCases() =>
    [
        static () => (Encoding.UTF8.GetBytes(WireBodyData.InfoOk), null),
        static () => (Utf8WithBom(), null),
        static () => (Encoding.UTF8.GetBytes(WireBodyData.InfoOk), "utf-8"),
        static () => (Encoding.UTF8.GetBytes(WireBodyData.InfoOk), "UTF-8"),
        static () => (Utf8WithBom(), "utf-8"),
#if NET
        // net472's own HttpContent rejects a quoted charset outright; the repo ships the
        // modern algorithm on every target (ADR-0014), so this row stays differential on
        // modern frameworks and the dedicated quoted-charset tests pin downlevel behavior.
        static () => (Encoding.UTF8.GetBytes(WireBodyData.InfoOk), "\"utf-8\""),
#endif
        static () => (Encoding.Unicode.GetBytes(WireBodyData.InfoOk), "utf-16"),
        static () => (WithPreamble(Encoding.Unicode), "utf-16"),
        static () => (WithPreamble(Encoding.Unicode), null),
        static () => (WithPreamble(Encoding.BigEndianUnicode), null),
        static () => (WithPreamble(Encoding.UTF32), null),
        static () => (WithPreamble(Encoding.UTF32), "utf-32"),
        static () => (WireBodyData.InfoWithMalformedUtf8UnknownField(), null),
        static () => (WireBodyData.InfoWithMalformedUtf8UnknownField(), "utf-8"),
        static () => (Encoding.UTF8.GetPreamble(), null),
        static () => ([], "not-an-encoding"),
    ];

    /// <summary>
    /// The parity contract itself: whatever real HttpContent string decoding produces for a
    /// body-and-charset pair on this target framework, the policy produces byte for byte —
    /// including replacement decoding of malformed UTF-8 (R09/R10 closure).
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ParityCases))]
    public async Task Decode_Should_Match_HttpContent_String_Decoding(byte[] body, string? charset)
    {
        using var content = new ByteArrayContent(body);
        if (charset is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = charset };
        }

        var expected = await content.ReadAsStringAsync();

        var actual = ResponseEncodingPolicy.Decode(body, charset).GetDecodedBody();

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task Decode_Should_Refuse_An_Invalid_Charset_Exactly_As_HttpContent_Does()
    {
        var body = Encoding.UTF8.GetBytes(WireBodyData.InfoOk);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "not-an-encoding" };

        _ = await Assert
            .That(async () => _ = await content.ReadAsStringAsync())
            .Throws<InvalidOperationException>();
        _ = Assert.Throws<InvalidOperationException>(() => _ = ResponseEncodingPolicy.Decode(body, "not-an-encoding"));
    }

    private static byte[] Utf8WithBom() =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(WireBodyData.InfoOk)];

    private static byte[] WithPreamble(Encoding encoding) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(WireBodyData.InfoOk)];
}
