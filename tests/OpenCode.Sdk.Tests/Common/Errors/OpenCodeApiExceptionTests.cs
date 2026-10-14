using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Common.Errors;

public sealed class OpenCodeApiExceptionTests
{
    [Test]
    public async Task Constructor_Should_Carry_Status_Error_And_RawBody()
    {
        var error = new UnauthorizedError
        {
            Message = "password required",
        };

        var exception = new OpenCodeApiException("The opencode API returned status 401.", 401, error, "{\"_tag\":\"UnauthorizedError\"}");

        await Assert.That(exception.Message).IsEqualTo("The opencode API returned status 401.");
        await Assert.That(exception.Status).IsEqualTo(401);
        await Assert.That(exception.Error).IsSameReferenceAs(error);
        await Assert.That(exception.RawBody).IsEqualTo("{\"_tag\":\"UnauthorizedError\"}");
    }

    [Test]
    public async Task Constructor_Should_Leave_Api_Data_Empty_On_The_Standard_Overloads()
    {
        var exception = new OpenCodeApiException("boom");

        await Assert.That(exception.Status).IsEqualTo(0);
        await Assert.That(exception.Error).IsNull();
        await Assert.That(exception.RawBody).IsNull();
    }

    [Test]
    public async Task Exception_Should_Derive_From_The_OpenCode_Base()
    {
        await Assert.That(new OpenCodeApiException()).IsAssignableTo<OpenCodeException>();
        await Assert.That(new OpenCodeTransportException()).IsAssignableTo<OpenCodeException>();
    }

    [Test]
    public async Task Constructor_Should_Preserve_The_Inner_Exception()
    {
        var inner = new InvalidOperationException("root cause");

        var exception = new OpenCodeApiException("boom", inner);

        await Assert.That(exception.InnerException).IsSameReferenceAs(inner);
    }
}
