using System.Text;
using OpenCode.Sdk.Internal.BackgroundService.Handoff;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.BackgroundService.Handoff;

/// <summary>
/// The strict sidecar boundary over embedded fixtures: the valid shape, the null handoff, the
/// absent id, unknown members ignored, and every shape failure — invalid UTF-8 included — read as
/// absent rather than thrown. Round-trip covers the writer.
/// </summary>
public sealed class PtyHandoffSidecarTests
{
    private static readonly FixtureLoader Fixtures = new();

    [Test]
    public async Task TryRead_Should_Decode_A_Valid_Sidecar()
    {
        var sidecar = Decode("BackgroundService.pty-handoff-valid.json");

        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.SourceId).IsEqualTo("srv_1");
        await Assert.That(sidecar.SourcePid).IsEqualTo(48213);
        await Assert.That(sidecar.SourceUrl).IsEqualTo("http://127.0.0.1:49374");
        await Assert.That(sidecar.ExpiresAt).IsEqualTo(1700000060000d);
        await Assert.That(sidecar.Handoff).IsNotNull();
        await Assert.That(sidecar.Handoff!.Value.GetProperty("ticket").GetString()).IsEqualTo("ticket-abc123");
    }

    [Test]
    public async Task TryRead_Should_Decode_A_Null_Handoff()
    {
        var sidecar = Decode("BackgroundService.pty-handoff-null-handoff.json");

        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.Handoff).IsNull();
    }

    [Test]
    public async Task TryRead_Should_Admit_A_Source_Without_An_Id()
    {
        var sidecar = Decode("BackgroundService.pty-handoff-source-without-id.json");

        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.SourceId).IsNull();
        await Assert.That(sidecar.SourcePid).IsEqualTo(48213);
    }

    [Test]
    public async Task TryRead_Should_Ignore_Unknown_Members()
    {
        var sidecar = Decode("BackgroundService.pty-handoff-unknown-members.json");

        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.Handoff).IsNotNull();
        await Assert.That(sidecar.Handoff!.Value.GetProperty("ticket").GetString()).IsEqualTo("ticket-abc123");
    }

    /// <summary>
    /// The pinned client's <c>read</c> checks only <c>typeof pid === "number"</c>, so a pid no process can
    /// have still makes a sidecar; it reads with no source pid, which no registration matches.
    /// </summary>
    [Test]
    public async Task TryRead_Should_Admit_A_Fractional_Pid_Without_A_Source_Pid()
    {
        var sidecar = Decode("BackgroundService.pty-handoff-fractional-pid.json");

        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.SourcePid).IsNull();
        await Assert.That(sidecar.SourceId).IsEqualTo("srv_1");
        await Assert.That(sidecar.SourceUrl).IsEqualTo("http://127.0.0.1:49374");
    }

    [Test]
    [Arguments("1.5")]
    [Arguments("2147483648")]
    [Arguments("9007199254740992")]
    [Arguments("1e300")]
    public async Task TryRead_Should_Read_Any_Number_That_Is_Not_A_Process_Id_As_No_Source_Pid(string pid)
    {
        var sidecar = DecodeBytes(Encoding.UTF8.GetBytes(
            "{\"source\":{\"pid\":" + pid + ",\"url\":\"http://127.0.0.1:49374\"},\"handoff\":null,\"expiresAt\":1700000060000}"));

        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.SourcePid).IsNull();
    }

    /// <summary>Every shape the pinned client's <c>read</c> refuses.</summary>
    [Test]
    [Arguments("BackgroundService.pty-handoff-invalid-handoff.json")]
    [Arguments("BackgroundService.pty-handoff-string-pid.json")]
    [Arguments("BackgroundService.pty-handoff-missing-source.json")]
    [Arguments("BackgroundService.pty-handoff-missing-expiry.json")]
    [Arguments("BackgroundService.pty-handoff-nonfinite-expiry.json")]
    [Arguments("BackgroundService.pty-handoff-array-root.json")]
    [Arguments("BackgroundService.pty-handoff-malformed.json")]
    public async Task TryRead_Should_Return_Null_For_A_Shape_Failure(string fixture)
    {
        await Assert.That(Decode(fixture)).IsNull();
    }

    [Test]
    public async Task ToUtf8Json_Should_Round_Trip_A_Sidecar()
    {
        var original = Decode("BackgroundService.pty-handoff-valid.json")!;

        var decoded = PtyHandoffSidecar.TryRead(original.ToUtf8Json());

        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded!.SourceId).IsEqualTo(original.SourceId);
        await Assert.That(decoded.SourcePid).IsEqualTo(original.SourcePid);
        await Assert.That(decoded.SourceUrl).IsEqualTo(original.SourceUrl);
        await Assert.That(decoded.ExpiresAt).IsEqualTo(original.ExpiresAt);
        await Assert.That(decoded.Handoff!.Value.GetRawText()).IsEqualTo(original.Handoff!.Value.GetRawText());
    }

    [Test]
    public async Task ToUtf8Json_Should_Omit_The_Id_Member_When_The_Source_Has_None()
    {
        var original = Decode("BackgroundService.pty-handoff-null-handoff.json")!;
        var withoutId = new PtyHandoffSidecar
        {
            SourceId = null,
            SourcePid = original.SourcePid,
            SourceUrl = original.SourceUrl,
            Handoff = original.Handoff,
            ExpiresAt = original.ExpiresAt,
        };

        var bytes = withoutId.ToUtf8Json();

        await Assert.That(DecodeBytes(bytes)!.SourceId).IsNull();
    }

    /// <summary>Only a sidecar built from a registration is published, and a registration always has a pid.</summary>
    [Test]
    public async Task ToUtf8Json_Should_Refuse_A_Sidecar_Without_A_Source_Pid()
    {
        var sidecar = Decode("BackgroundService.pty-handoff-fractional-pid.json")!;

        _ = await Assert.That(sidecar.ToUtf8Json).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TryRead_Should_Return_Null_For_A_String_That_Is_Not_Valid_Utf8()
    {
        // The reader accepts the token and throws only when the string is decoded: still not this
        // shape, so absent rather than a failure.
        var prefix = Encoding.UTF8.GetBytes("{\"source\":{\"pid\":48213,\"url\":\"http://127.0.0.1:49374\",\"id\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"},\"handoff\":null,\"expiresAt\":1700000060000}");

        await Assert.That(DecodeBytes([.. prefix, 0xC3, 0x28, .. suffix])).IsNull();
    }

    private static PtyHandoffSidecar? Decode(string fixture) =>
        DecodeBytes(Encoding.UTF8.GetBytes(Fixtures.LoadJson(fixture)));

    private static PtyHandoffSidecar? DecodeBytes(byte[] bytes) => PtyHandoffSidecar.TryRead(bytes);
}
