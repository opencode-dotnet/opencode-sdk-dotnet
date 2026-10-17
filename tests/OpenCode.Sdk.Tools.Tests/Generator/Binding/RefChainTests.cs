using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class RefChainTests
{
    [Test]
    [Arguments("terminal", "B")]
    [Arguments("missing", "A")]
    [Arguments("self-cycle", "A")]
    [Arguments("two-node-cycle", "B")]
    [Arguments("nullable", "B")]
    public async Task Resolve_Should_Return_The_Last_Node_And_Last_Successful_Key(string shape, string lastKey)
    {
        var lookups = 0;
        // A broken cycle guard fails deterministically, rather than hanging the test host.
        var comparer = EqualityComparer<string>.Create((left, right) => ++lookups > 32
            ? throw new InvalidOperationException("Reference traversal revisited the cycle")
            : StringComparer.Ordinal.Equals(left, right), StringComparer.Ordinal.GetHashCode);
        var terminal = new EnumNode { Values = ["open", "closed"] };
        SchemaNode expected = shape switch
        {
            "missing" => new RefNode { Target = "absent" },
            "self-cycle" or "two-node-cycle" => new RefNode { Target = "A" },
            "nullable" => new NullableNode { Inner = terminal },
            _ => terminal,
        };
        var graph = new Dictionary<string, SchemaNode>(comparer)
        {
            ["A"] = shape is "missing" or "self-cycle" ? expected : new RefNode { Target = "B" },
            ["B"] = expected,
        };

        var result = RefChain.Resolve(new RefNode { Target = "A" }, graph);

        await Assert.That(result.Node).IsSameReferenceAs(expected);
        await Assert.That(result.LastKey).IsEqualTo(lastKey);
    }

    [Test]
    public async Task Resolve_Should_Not_Unwrap_A_Nullable_Node()
    {
        var node = new NullableNode { Inner = new RefNode { Target = "A" } };
        var result = RefChain.Resolve(node, new Dictionary<string, SchemaNode>(StringComparer.Ordinal));
        await Assert.That(result.Node).IsSameReferenceAs(node);
        await Assert.That(result.LastKey).IsNull();
    }
}
