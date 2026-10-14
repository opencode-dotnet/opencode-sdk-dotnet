using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// A structural union is a record whose inactive arm properties throw by design, so the
/// compiler-synthesized ToString() would throw; the generated override prints the kind and the
/// active arm only. The first case is the reproduction that surfaced the defect.
/// </summary>
public sealed class StructuralUnionPrintingTests
{
    [Test]
    public async Task FormValue_Should_Print_Its_Active_Arm()
    {
        await Assert.That(FormValue.FromText("x").ToString()).IsEqualTo("FormValue { Kind = Text, Text = x }");
        await Assert.That(FormValue.FromNumber(42.5).ToString()).IsEqualTo("FormValue { Kind = Number, Number = 42.5 }");
        await Assert.That(FormValue.FromBoolean(false).ToString()).IsEqualTo("FormValue { Kind = Boolean, Boolean = False }");
        await Assert.That(FormValue.FromTextList(["a", "b"]).ToString()).IsEqualTo("FormValue { Kind = TextList, TextList = [a, b] }");
        await Assert.That(FormValue.FromUnknown(Parse("{\"future\": 1}")).ToString()).IsEqualTo("FormValue { Kind = Unknown, Unknown = {\"future\": 1} }");
    }

    [Test]
    public async Task Every_Shipped_Union_Should_Print_Its_Active_Arm()
    {
        await Assert.That(FormWhenValue.FromText("draft").ToString()).IsEqualTo("FormWhenValue { Kind = Text, Text = draft }");
        await Assert.That(McpRemoteConfigOauth.FromBoolean(true).ToString()).IsEqualTo("McpRemoteConfigOauth { Kind = Boolean, Boolean = True }");
        await Assert.That(ProviderSettingsTimeout.FromNumber(30000).ToString()).IsEqualTo("ProviderSettingsTimeout { Kind = Number, Number = 30000 }");
        await Assert.That(ProviderSettingsTimeout.FromBoolean(false).ToString()).IsEqualTo("ProviderSettingsTimeout { Kind = Boolean, Boolean = False }");
    }

    [Test]
    public async Task Printer_Should_Format_Numbers_With_The_Invariant_Culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            await Assert.That(ProviderSettingsTimeout.FromNumber(1234.5).ToString()).IsEqualTo("ProviderSettingsTimeout { Kind = Number, Number = 1234.5 }");
            await Assert.That(StructuralUnionPrinter.Format("Probe", "Number", 1234.5)).IsEqualTo("Probe { Kind = Number, Number = 1234.5 }");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Test]
    public async Task Printer_Should_Render_Each_Value_Kind()
    {
        await Assert.That(StructuralUnionPrinter.Format("Probe", "Text", "hello")).IsEqualTo("Probe { Kind = Text, Text = hello }");
        await Assert.That(StructuralUnionPrinter.Format("Probe", "Boolean", true)).IsEqualTo("Probe { Kind = Boolean, Boolean = True }");
        await Assert.That(StructuralUnionPrinter.Format("Probe", "Count", 7L)).IsEqualTo("Probe { Kind = Count, Count = 7 }");
        await Assert.That(StructuralUnionPrinter.Format("Probe", "TextList", new List<string> { "a", "b" })).IsEqualTo("Probe { Kind = TextList, TextList = [a, b] }");
        await Assert.That(StructuralUnionPrinter.Format("Probe", "Numbers", new[] { 1.5, 2 })).IsEqualTo("Probe { Kind = Numbers, Numbers = [1.5, 2] }");
        await Assert.That(StructuralUnionPrinter.Format("Probe", "Unknown", Parse("[1, {\"a\": null}]"))).IsEqualTo("Probe { Kind = Unknown, Unknown = [1, {\"a\": null}] }");
        await Assert.That(StructuralUnionPrinter.Format("Probe", "Other", new Uri("http://127.0.0.1:4096/"))).IsEqualTo("Probe { Kind = Other, Other = http://127.0.0.1:4096/ }");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
