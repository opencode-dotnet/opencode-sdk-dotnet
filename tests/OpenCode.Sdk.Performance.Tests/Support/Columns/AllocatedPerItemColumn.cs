using BenchmarkDotNet.Columns;

namespace OpenCode.Sdk.Performance.Tests.Support.Columns;

/// <summary>Exact allocated bytes divided by the items one operation consumes.</summary>
internal sealed class AllocatedPerItemColumn : WireFixtureColumn
{
    public override string ColumnName => "Alloc B/item";

    public override int PriorityInCategory => 3;

    public override UnitType UnitType => UnitType.Size;

    public override string Legend => "Allocated bytes per operation divided by items consumed";

    protected override string? Format(WireFixture fixture, long? allocatedBytes) =>
        allocatedBytes is { } allocated && fixture.Items > 0 ? Ratio(allocated / (double)fixture.Items) : null;
}
