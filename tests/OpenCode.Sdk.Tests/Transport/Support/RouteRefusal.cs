namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>Checks that a route refusal has the exact argument-exception type, parameter name and BCL-formatted message.</summary>
internal static class RouteRefusal
{
    public static async Task AssertAsync(ArgumentException? refusal, string reason, string parameterName)
    {
        await Assert.That(refusal!.GetType()).IsEqualTo(typeof(ArgumentException));
        await Assert.That(refusal.ParamName).IsEqualTo(parameterName);
        await Assert.That(refusal.Message).IsEqualTo(new ArgumentException(reason, parameterName).Message);
    }
}
