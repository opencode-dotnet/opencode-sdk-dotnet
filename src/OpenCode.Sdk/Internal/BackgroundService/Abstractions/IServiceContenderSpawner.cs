namespace OpenCode.Sdk.Internal;

/// <summary>
/// The detached-spawn seam Ensure elections start contenders through: the managed port of the
/// pinned client's <c>spawnServiceContender</c> (<c>service-contender.ts</c>), with Node/libuv's
/// <c>detached: true</c> expressed in platform primitives rather than
/// <see cref="System.Diagnostics.Process"/>, which cannot detach on any current target
/// (ADR-0027). Windows spawns through <c>CreateProcessW</c> with
/// <c>DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP</c>, NUL stdin/stdout, and an inheritable
/// stderr pipe under an explicit handle list; Unix spawns through <c>posix_spawnp</c> with
/// <c>POSIX_SPAWN_SETSID</c>, <c>/dev/null</c> stdin/stdout, and a stderr pipe. A missing
/// executable reports synchronously, and the seam refuses rather than falls back when the
/// platform cannot detach.
/// </summary>
internal interface IServiceContenderSpawner
{
    /// <summary>Starts one detached contender and returns its observation handle.</summary>
    /// <param name="startInfo">What to spawn, with which argv and overlaid environment.</param>
    /// <returns>The contender; its pid names the spawned process, or the cmd.exe host for a batch shim.</returns>
    /// <exception cref="OpenCodeServerException">The platform cannot detach, or the spawn itself failed; the inner exception carries the Win32 or errno failure.</exception>
    public IServiceContender Spawn(ContenderStartInfo startInfo);

    /// <summary>
    /// Everything one spawn names: what to run, not how a platform spells it. The spawner renders
    /// the platform form, the way the standalone launcher does — a Windows batch shim runs through
    /// the system cmd.exe on the launcher's <c>BatchCommandLine</c> line, anything else on the
    /// MSVCRT-quoted argv; Unix gets the argv as is and refuses a batch shim. <c>Environment</c>
    /// is the channel-plus-caller overlay, with the handoff variable applied last, that the spawner
    /// layers over the launching process's own environment; a null value removes the variable
    /// rather than passing it empty.
    /// </summary>
    /// <param name="Executable">The launcher resolution of the command's first entry; a failure names the caller's spelling.</param>
    /// <param name="Arguments">The command's remaining entries, in order.</param>
    /// <param name="Environment">The overlay: channel service-config env, then caller env, then the handoff.</param>
    public sealed record ContenderStartInfo(
        ResolvedExecutable Executable,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string?> Environment);
}
