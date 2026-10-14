namespace OpenCode.Sdk.Tests.Common.Options;

public sealed class OpenCodeRequestOptionsTests
{
    [Test]
    public async Task Constructor_Should_Default_To_Throwing_Behavior()
    {
        var options = new OpenCodeRequestOptions();

        await Assert.That(options.ErrorBehavior).IsEqualTo(ErrorBehavior.Default);
    }

    [Test]
    public async Task NoThrow_Should_Select_The_NoThrow_Behavior()
    {
        await Assert.That(OpenCodeRequestOptions.NoThrow.ErrorBehavior).IsEqualTo(ErrorBehavior.NoThrow);
    }

    [Test]
    public async Task NoThrow_Should_Return_A_Shared_Instance()
    {
        await Assert.That(OpenCodeRequestOptions.NoThrow).IsSameReferenceAs(OpenCodeRequestOptions.NoThrow);
    }

    [Test]
    public async Task Constructor_Should_Default_To_No_Per_Call_Location()
    {
        var options = new OpenCodeRequestOptions();

        await Assert.That(options.Location).IsNull();
    }

    [Test]
    public async Task Location_Should_Retain_The_Assigned_Selector()
    {
        var location = new LocationSelector { Directory = "/repo" };

        var options = new OpenCodeRequestOptions { Location = location };

        await Assert.That(options.Location).IsSameReferenceAs(location);
    }
}
