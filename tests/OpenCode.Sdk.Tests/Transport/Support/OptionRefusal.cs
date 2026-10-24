namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>Checks that an option setter refuses a value with the BCL range-exception message, parameter name and actual value.</summary>
internal static class OptionRefusal
{
    public static async Task AssertAsync(Action setter, object actualValue, string reason, string parameterName)
    {
        var refusal = await Assert.That(setter).Throws<ArgumentOutOfRangeException>();

        var expectedMessage = new ArgumentOutOfRangeException(parameterName, actualValue, reason).Message;
        await Assert.That(refusal!.Message).IsEqualTo(expectedMessage);
        await Assert.That(refusal.ParamName).IsEqualTo(parameterName);
        await Assert.That(refusal.ActualValue).IsEqualTo(actualValue);
    }
}
