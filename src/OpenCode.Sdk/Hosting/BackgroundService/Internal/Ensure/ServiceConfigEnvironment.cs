namespace OpenCode.Sdk.Internal;

/// <summary>The saved environment overlay; null entries mean the read captured no usable config.</summary>
/// <param name="Entries">The saved map without XDG_STATE_HOME, or null.</param>
internal sealed record ServiceConfigEnvironment(IReadOnlyDictionary<string, string>? Entries);
