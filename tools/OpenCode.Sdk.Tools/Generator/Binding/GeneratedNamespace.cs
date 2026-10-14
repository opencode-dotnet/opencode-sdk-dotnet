namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The three namespaces generated code declares. Public types keep <see cref="Public"/> or
/// <see cref="Models"/> wherever their file lives; every internal type declares <see cref="Internal"/>,
/// with no sub-namespaces, so moving an internal file never changes a namespace.
/// </summary>
internal static class GeneratedNamespace
{
    public const string Public = "OpenCode.Sdk";

    public const string Models = "OpenCode.Sdk.Models";

    public const string Internal = "OpenCode.Sdk.Internal";
}
