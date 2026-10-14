namespace OpenCode.Sdk.Tools.Tests.Packaging;

public sealed class GeneratedSuppressionTargetClassifierTests
{
    private readonly HashSet<string> _generated = new(StringComparer.Ordinal) { "OpenCode.Sdk.Models.AgentInfo" };

    [Test]
    [Arguments("T:OpenCode.Sdk.Models.AgentInfo")]
    [Arguments("M:OpenCode.Sdk.Models.AgentInfo.get_Id")]
    [Arguments("P:OpenCode.Sdk.Models.AgentInfo.Id")]
    [Arguments("F:OpenCode.Sdk.Models.AgentInfo.Id")]
    [Arguments("E:OpenCode.Sdk.Models.AgentInfo.Changed")]
    [Arguments("T:OpenCode.Sdk.Models.AgentInfo.Nested")]
    [Arguments("M:OpenCode.Sdk.Models.AgentInfo.Nested.Update(System.String,System.Int32)")]
    public async Task BelongsToGeneratedType_Should_Recognize_Type_And_Member_Targets(string target)
    {
        var generated = new GeneratedSuppressionTargetClassifier(_generated).BelongsToGeneratedType(target);

        await Assert.That(generated).IsTrue();
    }

    [Test]
    [Arguments("N:OpenCode.Sdk.Models")]
    [Arguments("OpenCode.Sdk.Models.AgentInfo")]
    [Arguments("M:NoDeclaringType")]
    public async Task BelongsToGeneratedType_Should_Refuse_Unsupported_Targets(string target)
    {
        await Assert.That(() => new GeneratedSuppressionTargetClassifier(_generated).BelongsToGeneratedType(target))
            .Throws<InvalidOperationException>();
    }
}
