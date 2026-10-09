using OpenCode.Sdk.Internal.Launcher;

namespace OpenCode.Sdk.Tests.Launcher;

/// <summary>
/// A server's environment: the host's variables, then the caller's entries, then the lease
/// credential, with names compared the way the platform compares them (case-sensitively on Linux
/// and macOS, ignoring case on Windows) and a NUL refused before it could cut an entry short.
/// </summary>
public sealed class ChildEnvironmentComposerTests
{
    private const string Password = "lease-credential";

    [Test]
    public async Task Compose_Should_Layer_The_Entries_Over_The_Host_And_The_Credential_Over_Both()
    {
        var composer = new ChildEnvironmentComposer(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = "/home/host",
            ["SHARED"] = "host",
            [ChildEnvironmentComposer.PasswordVariable] = "host-password",
        }, StringComparer.Ordinal);

        var composed = composer.Compose(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SHARED"] = "entry",
                ["EXTRA"] = "entry",
                [ChildEnvironmentComposer.PasswordVariable] = "entry-password",
            },
            Password);

        await Assert.That(composed["HOME"]).IsEqualTo("/home/host");
        await Assert.That(composed["SHARED"]).IsEqualTo("entry");
        await Assert.That(composed["EXTRA"]).IsEqualTo("entry");
        await Assert.That(composed[ChildEnvironmentComposer.PasswordVariable]).IsEqualTo(Password);
    }

    [Test]
    public async Task Compose_Should_Compare_Names_Case_Sensitively()
    {
        var composer = new ChildEnvironmentComposer(new Dictionary<string, string>(StringComparer.Ordinal) { ["Cased"] = "host" }, StringComparer.Ordinal);

        var composed = composer.Compose(new Dictionary<string, string>(StringComparer.Ordinal) { ["CASED"] = "entry" }, Password);

        await Assert.That(composed["Cased"]).IsEqualTo("host");
        await Assert.That(composed["CASED"]).IsEqualTo("entry");
    }

    [Test]
    [Arguments("NAME\0CUT", "value")]
    [Arguments("NAME", "value\0cut")]
    public async Task Compose_Should_Refuse_A_Nul_In_An_Entry(string name, string value)
    {
        var composer = new ChildEnvironmentComposer(new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);

        var refusal = await Assert.That(() => composer.Compose(new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value }, Password))
            .Throws<ArgumentException>();

        await Assert.That(refusal!.Message).Contains("NUL");
    }

    [Test]
    public async Task Compose_Should_Drop_A_Host_Variable_That_Holds_A_Nul()
    {
        var composer = new ChildEnvironmentComposer(new Dictionary<string, string>(StringComparer.Ordinal) { ["BROKEN"] = "a\0b", ["KEPT"] = "yes" }, StringComparer.Ordinal);

        var composed = composer.Compose(entries: null, Password);

        await Assert.That(composed.ContainsKey("BROKEN")).IsFalse();
        await Assert.That(composed["KEPT"]).IsEqualTo("yes");
    }

    /// <summary>
    /// Windows names ignore case: a caller entry spelled in another case replaces the host's entry
    /// instead of passing a second one the child's runtime would read in place of the first, and a
    /// differently cased credential entry cannot shadow the lease.
    /// </summary>
    [Test]
    public async Task Compose_Should_Replace_A_Host_Entry_Spelled_In_Another_Case_When_Names_Ignore_Case()
    {
        var composer = new ChildEnvironmentComposer(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Path"] = "host" },
            StringComparer.OrdinalIgnoreCase);

        var composed = composer.Compose(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = "entry",
                ["opencode_password"] = "entry-password",
            },
            Password);

        await Assert.That(composed.Count).IsEqualTo(2);
        await Assert.That(composed["path"]).IsEqualTo("entry");
        await Assert.That(composed[ChildEnvironmentComposer.PasswordVariable]).IsEqualTo(Password);
    }
}
