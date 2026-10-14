using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.BackgroundService;

/// <summary>
/// The user-home rule ported from libuv's <c>os.homedir()</c>: the platform variable first, the
/// known-folder API only as the fallback. .NET's <c>SpecialFolder.UserProfile</c> consults the
/// known-folder API first, so a redirected <c>USERPROFILE</c> would otherwise move the spawned
/// CLI's roots but not the SDK's.
/// </summary>
public sealed class ServiceEnvironmentTests
{
    [Test]
    public async Task ResolveUserProfile_Should_Prefer_USERPROFILE_On_Windows()
    {
        var home = ServiceEnvironment.ResolveUserProfile(
            isWindows: true, userProfileVariable: "C:\\redirected", homeVariable: "/ignored", specialFolder: "C:\\Users\\real");

        await Assert.That(home).IsEqualTo("C:\\redirected");
    }

    [Test]
    public async Task ResolveUserProfile_Should_Prefer_HOME_On_Unix()
    {
        var home = ServiceEnvironment.ResolveUserProfile(
            isWindows: false, userProfileVariable: "C:\\ignored", homeVariable: "/home/redirected", specialFolder: "/home/real");

        await Assert.That(home).IsEqualTo("/home/redirected");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResolveUserProfile_Should_Fall_Back_To_The_Special_Folder_When_The_Variable_Is_Empty(bool isWindows)
    {
        var home = ServiceEnvironment.ResolveUserProfile(isWindows, userProfileVariable: "", homeVariable: "", specialFolder: "/fallback");

        await Assert.That(home).IsEqualTo("/fallback");
    }

    [Test]
    public async Task ResolveUserProfile_Should_Return_Null_When_Nothing_Resolves()
    {
        var home = ServiceEnvironment.ResolveUserProfile(isWindows: false, userProfileVariable: null, homeVariable: null, specialFolder: "");

        await Assert.That(home).IsNull();
    }
}
