using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigJsonWriterTests
{
    [Test]
    public async Task Write_Should_Keep_Empty_Containers_Compact()
    {
        var document = new ServiceConfigDocument(corsOrigins: [], environment: new Dictionary<string, string>(StringComparer.Ordinal));
        await Assert.That(Encoding.UTF8.GetString(ServiceConfigJsonWriter.Write(document)))
            .IsEqualTo("{\n  \"cors\": [],\n  \"env\": {}\n}\n");
    }

    [Test]
    public async Task Write_Should_Order_Only_Canonical_Array_Index_Names_First()
    {
        var document = new ServiceConfigDocument(
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["4294967295"] = "not-index",
                ["01"] = "not-canonical",
                ["4294967294"] = "last-index",
                ["0"] = "first-index",
            });
        await Assert.That(Encoding.UTF8.GetString(ServiceConfigJsonWriter.Write(document))).IsEqualTo(
            "{\n  \"env\": {\n    \"0\": \"first-index\",\n    \"4294967294\": \"last-index\",\n"
            + "    \"4294967295\": \"not-index\",\n    \"01\": \"not-canonical\"\n  }\n}\n");
    }

    [Test]
    public async Task Write_Should_Use_Lowercase_Escapes_For_Controls_And_Lone_Surrogates()
    {
        var document = new ServiceConfigDocument(hostname: "\ud800\u001f\"\\/\b\f\n\r\té😀\u2028");
        var text = Encoding.UTF8.GetString(ServiceConfigJsonWriter.Write(document));
        await Assert.That(text).IsEqualTo("{\n  \"hostname\": \"\\ud800\\u001f\\\"\\\\/\\b\\f\\n\\r\\té😀\u2028\"\n}\n");
    }

    [Test]
    public async Task Write_Should_Order_Array_Index_Names_And_Keep_Other_Names_In_Order()
    {
        var document = new ServiceConfigDocument(
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["B"] = "b",
                ["10"] = "ten",
                ["2"] = "two",
                ["01"] = "one",
            });
        await Assert.That(Encoding.UTF8.GetString(ServiceConfigJsonWriter.Write(document))).IsEqualTo(
            "{\n  \"env\": {\n    \"2\": \"two\",\n    \"10\": \"ten\",\n    \"B\": \"b\",\n    \"01\": \"one\"\n  }\n}\n");
    }
}
