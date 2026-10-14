namespace OpenCode.Sdk.Internal;

/// <summary>
/// What <see cref="ExecutableResolver"/> decided to spawn: the caller's original spelling (kept so
/// a failure can name what they wrote), the path the process actually starts, and whether that
/// path is a Windows batch shim, which changes how it has to be launched.
/// </summary>
/// <param name="Command">The command as the caller spelled it.</param>
/// <param name="Path">The path to spawn.</param>
/// <param name="IsBatchScript">Whether the path is a Windows <c>.cmd</c>/<c>.bat</c> script.</param>
internal sealed record ResolvedExecutable(string Command, string Path, bool IsBatchScript);
