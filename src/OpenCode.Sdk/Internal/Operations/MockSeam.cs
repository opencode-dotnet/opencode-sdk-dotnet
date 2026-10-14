namespace OpenCode.Sdk.Internal;

/// <summary>Builds the instructive failure every unoverridden mock-seam member throws.</summary>
internal static class MockSeam
{
    public static InvalidOperationException CreateError(string typeName, string memberName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        return new InvalidOperationException(
            $"'{typeName}.{memberName}' was invoked on an instance created through the protected mocking constructor; override the member under test.");
    }
}
