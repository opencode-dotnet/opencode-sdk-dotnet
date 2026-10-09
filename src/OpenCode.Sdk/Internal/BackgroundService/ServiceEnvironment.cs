using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using static OpenCode.Sdk.Internal.Windows.WindowsPlatform;

namespace OpenCode.Sdk.Internal.BackgroundService;

/// <summary>The real process environment behind <see cref="IServiceEnvironment"/>.</summary>
internal sealed class ServiceEnvironment : IServiceEnvironment
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? UserProfile => ResolveUserProfile(
        IsWindows,
        Environment.GetEnvironmentVariable("USERPROFILE"),
        Environment.GetEnvironmentVariable("HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// libuv's rule, which the pinned CLI inherits through <c>os.homedir()</c>: the platform
    /// variable wins when it is non-empty, and the profile folder is only the fallback. .NET's
    /// <see cref="Environment.SpecialFolder.UserProfile"/> consults the known-folder API first,
    /// so reading it alone would let a redirected home move the CLI's roots but not the SDK's.
    /// </summary>
    /// <param name="isWindows">Whether the Windows dialect applies.</param>
    /// <param name="userProfileVariable">The raw <c>USERPROFILE</c> value.</param>
    /// <param name="homeVariable">The raw <c>HOME</c> value.</param>
    /// <param name="specialFolder">The profile folder the runtime reports.</param>
    /// <returns>The home directory, or null when nothing resolves.</returns>
    internal static string? ResolveUserProfile(bool isWindows, string? userProfileVariable, string? homeVariable, string? specialFolder)
    {
        var preferred = isWindows ? userProfileVariable : homeVariable;
        if (!string.IsNullOrEmpty(preferred))
        {
            return preferred;
        }

        return string.IsNullOrEmpty(specialFolder) ? null : specialFolder;
    }

}
