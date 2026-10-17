using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class UnionMembershipValidatorTests
{
    [Test]
    public async Task Validate_Should_Refuse_The_Same_Marker_Name_With_Different_Kinds()
    {
        var model = new ObjectModelPlan
        {
            Name = "SharedEvent",
            Namespace = "OpenCode.Sdk.Models",
            Properties = [],
            ImplementedUnionNames = ["IBooleanEvent", "IStringEvent"],
        };
        var unions = new[] { Union("IBooleanEvent", LiteralKind.Boolean), Union("IStringEvent", LiteralKind.String), };
        var errors = new BindingErrorCollector();

        UnionMembershipValidator.Validate([model], unions, errors);
        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);

        await Assert.That(exception.Errors.Single().Category).IsEqualTo(BindingErrorCategory.Schema);
        await Assert.That(exception.Errors.Single().Subject).IsEqualTo("SharedEvent");
        await Assert.That(exception.Errors.Single().Problem).Contains("marker 'type'");
        await Assert.That(exception.Errors.Single().Problem).Contains("different kinds");
    }

    private static UnionPlan Union(string name, LiteralKind markerKind) =>
        new()
        {
            Name = name,
            ConceptName = name[1..],
            Namespace = "OpenCode.Sdk.Models",
            UnknownTypeName = $"Unknown{name[1..]}",
            MarkerWireName = "type",
            MarkerName = "Type",
            MarkerKind = markerKind,
            Variants = [],
        };
}
