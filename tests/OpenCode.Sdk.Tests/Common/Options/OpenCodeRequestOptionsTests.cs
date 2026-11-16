using System.Reflection;
using OpenCode.Sdk.Internal;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Common.Options;

public sealed class OpenCodeRequestOptionsTests
{
    [Test]
    public async Task Headers_Should_Snapshot_Every_Public_Instance_Option()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Carry"] = "sentinel" };
        var sentinels = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [nameof(OpenCodeRequestOptions.Headers)] = headers,
            [nameof(OpenCodeRequestOptions.ErrorBehavior)] = ErrorBehavior.NoThrow,
            [nameof(OpenCodeRequestOptions.Location)] = new LocationSelector { Directory = "/sentinel" },
            [nameof(OpenCodeRequestOptions.NetworkTimeout)] = TimeSpan.FromSeconds(42),
            [nameof(OpenCodeRequestOptions.InlineMedia)] = true,
        };
        var properties = typeof(OpenCodeRequestOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var options = new OpenCodeRequestOptions();
        foreach (var property in properties)
        {
            await Assert.That(sentinels).ContainsKey(property.Name);
            property.SetValue(options, sentinels[property.Name]);
        }

        var snapshot = OperationRequest.Describe<object>("test.operation", HttpMethod.Get, "/api/info",
            body: null, bodyTypeInfo: null, content: null, options, declaredHeaders: null).Options;
        await Assert.That(snapshot).IsNotNull();
        foreach (var property in properties)
        {
            if (property.Name is nameof(OpenCodeRequestOptions.Headers))
            {
                await Assert.That(snapshot!.Headers).IsEquivalentTo(headers, CollectionOrdering.Any);
            }
            else
            {
                await Assert.That(property.GetValue(snapshot)).IsEqualTo(sentinels[property.Name]);
            }
        }
    }

    [Test]
    public async Task Headers_Should_Default_To_Null()
    {
        var property = typeof(OpenCodeRequestOptions).GetProperty("Headers");
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(IReadOnlyDictionary<string, string>));
        await Assert.That(property.GetValue(new OpenCodeRequestOptions())).IsNull();
    }

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
