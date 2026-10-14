namespace OpenCode.Sdk.Internal;

/// <summary>
/// The files one discovery reads or may create, as the pinned CLI's <c>ServiceConfig.paths()</c>
/// lays them out.
/// </summary>
/// <param name="RegistrationFile">The registration the daemon publishes and discovery reads.</param>
/// <param name="ConfigFile">The service config beside it under the config root; null in direct-file mode.</param>
/// <param name="LegacyRegistrationFiles">The registration donors in the order the CLI tries them: the hashed legacy name, then the shared file for a custom channel; the first valid one wins because the target is created exclusively.</param>
/// <param name="LegacyConfigFile">The single hashed config donor, or null when the channel has no legacy name.</param>
internal sealed record ServicePaths(
    string RegistrationFile,
    string? ConfigFile,
    IReadOnlyList<string> LegacyRegistrationFiles,
    string? LegacyConfigFile);
