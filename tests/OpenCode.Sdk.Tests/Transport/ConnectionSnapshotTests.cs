using System.Net.Http.Headers;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class ConnectionSnapshotTests
{
    private const string EndpointBase = "http://localhost:4096";

    private const string Password = "s3cr3t-p455w0rd";

    private const string Username = "opencode";

    [Test]
    public async Task ToString_Should_Not_Carry_The_Credential()
    {
        var snapshot = new ConnectionSnapshot(EndpointBase, BasicCredential(), Location, new ManualTimeProvider());

        var rendered = Render(snapshot);

        // Basic is reversible base64, so neither the encoded header nor the password it decodes
        // to may reach a rendered string a log line or an exception message could carry.
        await Assert.That(rendered).DoesNotContain(Password);
        await Assert.That(rendered).DoesNotContain(EncodedCredential());
        await Assert.That(rendered).DoesNotContain("Basic");
    }

    /// <summary>
    /// Pins the shape that keeps the credential unprintable: the snapshot declares no
    /// <see cref="object.ToString"/> of its own, so it has no member-printing rendering for a
    /// credential to appear in. Turning it back into a record would synthesize one and fail here.
    /// </summary>
    [Test]
    public async Task The_Snapshot_Should_Declare_No_Member_Printing_Rendering()
    {
        var rendering = typeof(ConnectionSnapshot).GetMethod(nameof(ToString), Type.EmptyTypes);

        await Assert.That(rendering!.DeclaringType).IsEqualTo(typeof(object));
        await Assert.That(typeof(ConnectionSnapshot).GetMethod("PrintMembers")).IsNull();
    }

    [Test]
    public async Task The_Snapshot_Should_Still_Carry_Every_Member_It_Was_Built_With()
    {
        var credential = BasicCredential();

        var snapshot = new ConnectionSnapshot(EndpointBase, credential, Location, new ManualTimeProvider());

        // Redacting the rendering must not quietly redact the value itself.
        await Assert.That(snapshot.EndpointBase).IsEqualTo(EndpointBase);
        await Assert.That(snapshot.Authorization).IsSameReferenceAs(credential);
        await Assert.That(snapshot.Location!.Directory).IsEqualTo("/repo");
    }

    [Test]
    public async Task The_Snapshot_Should_Accept_An_Anonymous_Unlocated_Connection()
    {
        var snapshot = new ConnectionSnapshot(EndpointBase, authorization: null, location: null, new ManualTimeProvider());

        await Assert.That(snapshot.Authorization).IsNull();
        await Assert.That(snapshot.Location).IsNull();
    }

    [Test]
    public async Task The_Snapshot_Should_Refuse_A_Blank_Endpoint_Base()
    {
        _ = Assert.Throws<ArgumentException>(() => _ = new ConnectionSnapshot(" ", authorization: null, location: null, new ManualTimeProvider()));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Renders through reflection rather than a direct call: whichever <c>ToString</c> the type
    /// resolves to is the one that must not carry the credential, and a direct call would only
    /// re-state at compile time what this test is meant to prove at runtime.
    /// </summary>
    private static string Render(ConnectionSnapshot snapshot) =>
        (string)typeof(ConnectionSnapshot).GetMethod(nameof(ToString), Type.EmptyTypes)!.Invoke(snapshot, null)!;

    private static LocationSelector Location => new() { Directory = "/repo" };

    private static AuthenticationHeaderValue BasicCredential() => new("Basic", EncodedCredential());

    private static string EncodedCredential() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
}
