using System.Text;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

/// <summary>
/// The strict hand-written boundary over the daemon's registration file: the pinned shape decodes,
/// every other document is absent state, and the record never renders its password.
/// </summary>
public sealed class ServiceRegistrationReaderTests
{
    [Test]
    public async Task TryRead_Should_Decode_The_Pinned_Registration()
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(new FixtureLoader().LoadJson("BackgroundService.registration-modern.json")));

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.Id).IsEqualTo("srv_01J9Z7K8M4N2P6Q1R3S5T7V9W");
        await Assert.That(registration.Version).IsEqualTo("2.0.3");
        await Assert.That(registration.Url).IsEqualTo("http://127.0.0.1:49374");
        await Assert.That(registration.Endpoint).IsEqualTo(new Uri("http://127.0.0.1:49374"));
        await Assert.That(registration.ProcessId).IsEqualTo(48213);
        await Assert.That(registration.Password).IsEqualTo(ServiceRegistrationData.Password);
    }

    [Test]
    public async Task TryRead_Should_Accept_A_Registration_With_Only_The_Required_Members()
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(ServiceRegistrationData.Minimal));

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.Id).IsNull();
        await Assert.That(registration.Version).IsNull();
        await Assert.That(registration.Password).IsNull();
    }

    [Test]
    public async Task TryRead_Should_Report_A_Missing_Password_As_Null()
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(ServiceRegistrationData.Passwordless));

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.Password).IsNull();
    }

    /// <summary>
    /// Only a missing member means no credential (<c>info.password === undefined</c> in the pinned
    /// client); the daemon runs with <c>config.password || random</c>, so a whitespace password is
    /// one it really serves with, and the client sends whatever string the file holds.
    /// </summary>
    [Test]
    [Arguments(ServiceRegistrationData.BlankPassword, "  ")]
    [Arguments(ServiceRegistrationData.EmptyPassword, "")]
    public async Task TryRead_Should_Keep_A_Present_Password_As_Written(string json, string password)
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(json));

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.Password).IsEqualTo(password);
    }

    [Test]
    public async Task TryRead_Should_Skip_Unknown_Members()
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(ServiceRegistrationData.UnknownMembersSkipped));

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.ProcessId).IsEqualTo(48213);
    }

    /// <summary>
    /// The CLI's schema reads <c>pid</c> as <c>Number.isSafeInteger</c> over the value <c>JSON.parse</c>
    /// produced, so the notation an integer is written in does not matter.
    /// </summary>
    [Test]
    [Arguments(ServiceRegistrationData.PidWithZeroFraction)]
    [Arguments(ServiceRegistrationData.PidInExponentNotation)]
    public async Task TryRead_Should_Accept_An_Integral_Pid_In_Any_Number_Notation(string json)
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(json));

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.ProcessId).IsEqualTo(1234);
    }

    [Test]
    [Arguments(ServiceRegistrationData.ArrayRoot)]
    [Arguments(ServiceRegistrationData.MissingUrl)]
    [Arguments(ServiceRegistrationData.MissingPid)]
    [Arguments(ServiceRegistrationData.RelativeUrl)]
    [Arguments(ServiceRegistrationData.NonHttpUrl)]
    [Arguments(ServiceRegistrationData.ZeroPid)]
    [Arguments(ServiceRegistrationData.NegativePid)]
    [Arguments(ServiceRegistrationData.PidAboveInt32)]
    [Arguments(ServiceRegistrationData.FractionalPid)]
    [Arguments(ServiceRegistrationData.FractionalPidAboveOne)]
    [Arguments(ServiceRegistrationData.StringPid)]
    [Arguments(ServiceRegistrationData.NumericUrl)]
    [Arguments(ServiceRegistrationData.ObjectVersion)]
    [Arguments(ServiceRegistrationData.DuplicateUrl)]
    [Arguments(ServiceRegistrationData.DuplicatePid)]
    [Arguments(ServiceRegistrationData.Malformed)]
    [Arguments(ServiceRegistrationData.TrailingContent)]
    public async Task TryRead_Should_Treat_An_Invalid_Document_As_Absent(string json)
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(json));

        await Assert.That(registration).IsNull();
    }

    [Test]
    [Arguments("http://0.0.0.0:49374", "http://127.0.0.1:49374/")]
    [Arguments("http://[::]:49374", "http://[::1]:49374/")]
    public async Task TryRead_Should_Connect_An_Unspecified_Host_Over_Loopback_And_Keep_The_Url_Raw(string written, string connectTarget)
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes($$"""{"url":"{{written}}","pid":48213}"""));

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.Url).IsEqualTo(written);
        await Assert.That(registration.Endpoint).IsEqualTo(new Uri(connectTarget));
    }

    [Test]
    public async Task TryRead_Should_Read_A_Bom_Prefixed_Document()
    {
        var registration = ServiceRegistrationReader.TryRead([.. Utf8Bom, .. Bytes(ServiceRegistrationData.Minimal)]);

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.ProcessId).IsEqualTo(48213);
    }

    [Test]
    public async Task TryRead_Should_Treat_Invalid_Utf8_In_A_String_As_Absent()
    {
        // 0xC3 opens a two-byte sequence that 0x28 ('(') cannot continue.
        byte[] document = [.. Bytes("{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213,\"version\":\""), 0xC3, 0x28, .. Bytes("\"}")];

        var registration = ServiceRegistrationReader.TryRead(document);

        await Assert.That(registration).IsNull();
    }

    [Test]
    public async Task TryRead_Should_Treat_Empty_Input_As_Absent()
    {
        var registration = ServiceRegistrationReader.TryRead([]);

        await Assert.That(registration).IsNull();
    }

    [Test]
    public async Task ToString_Should_Not_Carry_The_Password()
    {
        var registration = ServiceRegistrationReader.TryRead(Bytes(new FixtureLoader().LoadJson("BackgroundService.registration-modern.json")));

        var rendered = Render(registration);

        await Assert.That(rendered).DoesNotContain(ServiceRegistrationData.Password);
        await Assert.That(rendered).DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + ServiceRegistrationData.Password)));
    }

    [Test]
    public async Task The_Registration_Should_Declare_No_Member_Printing_Rendering()
    {
        var rendering = typeof(ServiceRegistration).GetMethod(nameof(ToString), Type.EmptyTypes);

        await Assert.That(rendering!.DeclaringType).IsEqualTo(typeof(object));
        await Assert.That(typeof(ServiceRegistration).GetMethod("PrintMembers")).IsNull();
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>
    /// Renders through reflection rather than a direct call: whichever <c>ToString</c> the type
    /// resolves to is the one that must not carry the password.
    /// </summary>
    private static string Render(ServiceRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return (string)typeof(ServiceRegistration).GetMethod(nameof(ToString), Type.EmptyTypes)!.Invoke(registration, null)!;
    }
}
