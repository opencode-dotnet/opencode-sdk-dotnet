namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>An operation is experimental when upstream serves it under its experimental route prefix.</summary>
internal static class ExperimentalRoutePolicy
{
    public static bool IsExperimental(string routeTemplate) =>
        routeTemplate.StartsWith("/api/experimental/", StringComparison.Ordinal);
}
