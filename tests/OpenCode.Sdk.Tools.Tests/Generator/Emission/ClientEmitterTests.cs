using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ClientEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_Virtual_Delegating_Clients()
    {
        var sources = ClientEmitter.Emit(EmitterPlanFixture.CreateClientPlans());

        await Verify(EmitterSnapshot.Create(sources));
    }

    [Test]
    public async Task Emit_Should_Produce_Internal_Raw_Clients()
    {
        var sources = ClientEmitter.Emit(EmitterPlanFixture.CreateInternalRawClientPlans());

        await Verify(EmitterSnapshot.Create(sources));
    }

    [Test]
    public async Task Emit_Should_Document_The_Operation_Id_And_Route_On_Each_Operation_Member()
    {
        var source = EmitSource(EmitterPlanFixture.CreateClientPlans(), "Widgets/WidgetClient.cs");

        await Assert.That(source).Contains(
            "    /// <remarks>\n"
            + "    /// Operation <c>widget.item.get</c>: <c>GET /api/widget/{widgetID}/item/{itemID}</c>.\n"
            + "    /// </remarks>\n"
            + "    public virtual Task<WidgetItemResponse> GetItemAsync(");
        await Assert.That(source).Contains(
            "    /// <remarks>\n"
            + "    /// Operation <c>widget.item.list</c>: <c>GET /api/widget/{widgetID}/item</c>.\n"
            + "    /// </remarks>\n"
            + "    public virtual CursorSequence<WidgetItemListResponse, ExampleItem> EnumerateItemsAsync(");
    }

    [Test]
    public async Task Emit_Should_Close_Every_Door_On_An_Internal_Raw_Client()
    {
        var source = EmitInternalRawSource("Ptys/Internal/PtyRawClient.cs");

        await Assert.That(source).Contains("internal sealed class PtyRawClient");
        await Assert.That(source).Contains("internal Task<PtyConnectTokenResponse> CreateConnectTokenAsync(");
        await Assert.That(source).DoesNotContain("public ");
        await Assert.That(source).DoesNotContain("protected ");
        await Assert.That(source).DoesNotContain("virtual ");
    }

    [Test]
    public async Task Emit_Should_Keep_An_Internal_Raw_Handle_Factory_Internal()
    {
        var source = EmitInternalRawSource("Ptys/Internal/PtysRawClient.cs");

        await Assert.That(source).Contains("internal sealed class PtysRawClient");
        await Assert.That(source).Contains("internal PtyRawClient GetPtyRawClient(string ptyId)");
        await Assert.That(source).DoesNotContain("public ");
        await Assert.That(source).DoesNotContain("protected ");
    }

    [Test]
    public async Task Emit_Should_Pass_A_Declared_Header_Into_The_Pipeline()
    {
        var source = EmitInternalRawSource("Ptys/Internal/PtyRawClient.cs");

        await Assert.That(source).Contains("string? xOpencodeTicket = null");
        await Assert.That(source).Contains("var declaredHeaders = new List<DeclaredHeader>(1);");
        await Assert.That(source).Contains("declaredHeaders.Add(new DeclaredHeader(\"x-opencode-ticket\", xOpencodeTicket));");
        await Assert.That(source).Contains("declaredHeaders: declaredHeaders");
    }

    [Test]
    public async Task Emit_Should_Keep_The_Public_Family_Accessor_For_An_Internal_Raw_Group()
    {
        var source = EmitInternalRawSource("OpenCodeClient.cs");

        await Assert.That(source).Contains("public virtual PtysClient Ptys");
        await Assert.That(source).Contains("new PtysClient(_pipeline)");
        await Assert.That(source).DoesNotContain("RawClient");
    }

    private static string EmitInternalRawSource(string relativePath) =>
        EmitSource(EmitterPlanFixture.CreateInternalRawClientPlans(), relativePath);

    private static string EmitSource(IReadOnlyList<ClientPlan> plans, string relativePath) =>
        Encoding.UTF8.GetString(ClientEmitter
            .Emit(plans)
            .Single(source => string.Equals(source.RelativePath, relativePath, StringComparison.Ordinal))
            .Utf8Source.Span);
}
