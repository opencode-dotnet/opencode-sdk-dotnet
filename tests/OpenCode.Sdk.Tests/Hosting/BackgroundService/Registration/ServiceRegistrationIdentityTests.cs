using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;

/// <summary>
/// The pinned client's <c>same()</c>: the four registration fields decide, the password never does.
/// </summary>
public sealed class ServiceRegistrationIdentityTests
{
    [Test]
    public async Task Of_Should_Take_The_Four_Registration_Fields_And_Ignore_The_Password()
    {
        var withPassword = Read(ServiceRegistrationData.PasswordedTwin);
        var without = Read(ServiceRegistrationData.Passwordless);

        var identity = ServiceRegistrationIdentity.Of(withPassword);

        await Assert.That(identity).IsEqualTo(new ServiceRegistrationIdentity("srv_1", "2.0.3", "http://127.0.0.1:49374", 48213));
        await Assert.That(identity).IsEqualTo(ServiceRegistrationIdentity.Of(without));
    }

    [Test]
    [Arguments(ServiceRegistrationData.Minimal)]
    [Arguments(ServiceRegistrationData.DevPrerelease)]
    [Arguments(ServiceRegistrationData.DuplicatePidResolved)]
    public async Task Identity_Should_Differ_When_A_Registration_Field_Differs(string other)
    {
        var identity = ServiceRegistrationIdentity.Of(Read(ServiceRegistrationData.Passwordless));

        await Assert.That(identity).IsNotEqualTo(ServiceRegistrationIdentity.Of(Read(other)));
    }

    private static ServiceRegistration Read(string document) =>
        ServiceRegistrationReader.TryRead(Encoding.UTF8.GetBytes(document))
        ?? throw new InvalidOperationException("The test document must decode.");
}
