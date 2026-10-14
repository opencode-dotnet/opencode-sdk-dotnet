namespace OpenCode.Sdk.Tools.Tests.Support;

internal sealed class ExperimentalOperationScenario(bool stable = false) : SpecScenario
{
    protected override void Arrange(SpecDocumentBuilder spec)
    {
        spec.WithSchema("ProbeInfo", static schema => schema.Type("object").Property("value", static value => value.Type("string")))
            .WithOperation("probe.get", path: "/api/experimental/probe", configure: static operation => operation
                .Parameter("search", "query", static schema => schema.Type("string"))
                .Response(200, "application/json", static schema => schema.Ref("ProbeInfo")));
        if (stable)
        {
            spec.WithOperation("probe.list", path: "/api/probes", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("ProbeInfo")));
        }
    }
}
