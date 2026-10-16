using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Projection;

public sealed class TemplateLiteralPrefixPolicyTests
{
    [Test]
    [Arguments("^rpc\\.[\\s\\S]*?$", "rpc.")]
    [Arguments("^a\\/b\\\\c\\^d\\$e\\*f\\+g\\?h\\.i\\(j\\)k\\|l\\[m\\]n\\{o\\}p[\\s\\S]*?$", "a/b\\c^d$e*f+g?h.i(j)k|l[m]n{o}p")]
    [Arguments("^plain-name_1:[\\s\\S]*?$", "plain-name_1:")]
    public async Task TryDecodePrefix_Should_Decode_Effect_Template_Literal_Prefixes(string pattern, string expected)
    {
        await Assert.That(TemplateLiteralPrefixPolicy.TryDecodePrefix(pattern)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, "null pattern")]
    [Arguments("", "empty")]
    [Arguments("^[\\s\\S]*?$", "empty literal")]
    [Arguments("^rpc\\.[\\s\\S]*$", "greedy span")]
    [Arguments("^rpc\\.[\\s\\S]*?", "missing end anchor")]
    [Arguments("rpc\\.[\\s\\S]*?$", "missing start anchor")]
    [Arguments("^rpc\\.[\\s\\S]*?x$", "literal after the span")]
    [Arguments("^rpc\\.[+-]?\\d*\\.?\\d+(?:[Ee][+-]?\\d+)?[\\s\\S]*?$", "number span")]
    [Arguments("^a|b[\\s\\S]*?$", "union alternative")]
    [Arguments("^a[\\s\\S]*?b[\\s\\S]*?$", "nested string span")]
    [Arguments("^rpc\\-[\\s\\S]*?$", "escape of a non-metacharacter")]
    [Arguments("^rpc\\[\\s\\S]*?$", "trailing backslash swallows the span")]
    [Arguments("^evt_", "validation prefix without a span")]
    [Arguments("^[a-f0-9]{64}$", "character class")]
    public async Task TryDecodePrefix_Should_Refuse_Everything_Else(string? pattern, string reason)
    {
        await Assert.That(TemplateLiteralPrefixPolicy.TryDecodePrefix(pattern)).IsNull().Because(reason);
    }
}
