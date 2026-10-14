namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// Values the native AOT consumer smoke shares between the fixture that writes its source and the
/// tests that prove its output.
/// </summary>
internal static class AotConsumerData
{
    /// <summary>The single line the native executable prints when both packages ran under native AOT.</summary>
    public const string Marker = "aot-smoke-ok";
}
