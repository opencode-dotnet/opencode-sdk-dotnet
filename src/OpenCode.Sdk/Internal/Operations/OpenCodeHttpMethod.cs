namespace OpenCode.Sdk.Internal;

/// <summary>
/// Verb singletons the downlevel BCL does not expose; <c>HttpMethod.Patch</c> only exists
/// on modern targets, so generated methods reference this spine instead.
/// </summary>
internal static class OpenCodeHttpMethod
{
    public static HttpMethod Patch { get; } = new HttpMethod("PATCH");
}
