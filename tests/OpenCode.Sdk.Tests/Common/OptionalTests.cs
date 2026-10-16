namespace OpenCode.Sdk.Tests.Common;

/// <summary>
/// The carrier's own contract, independent of any wire shape: which of the three states each
/// spelling produces, and the equality the serializer relies on — <c>WhenWritingDefault</c> omits
/// a member by comparing it with <c>default</c> through <see cref="EqualityComparer{T}" />, so a
/// wrong <c>Equals</c> here would silently drop set members from every request body.
/// </summary>
public sealed class OptionalTests
{
    [Test]
    public async Task Default_Should_Be_Absent()
    {
        Optional<string?> absent = default;

        await Assert.That(absent.IsSet).IsFalse();
        await Assert.That(absent.Value).IsNull();
    }

    [Test]
    public async Task Null_Should_Be_Set_To_Null()
    {
        var cleared = Optional<string?>.Null;

        await Assert.That(cleared.IsSet).IsTrue();
        await Assert.That(cleared.Value).IsNull();
    }

    [Test]
    public async Task Constructor_Should_Set_The_Carried_Value()
    {
        var carried = new Optional<string?>("pwsh");

        await Assert.That(carried.IsSet).IsTrue();
        await Assert.That(carried.Value).IsEqualTo("pwsh");
    }

    [Test]
    public async Task Implicit_Conversion_Should_Set_A_Value_And_An_Explicit_Null()
    {
        Optional<string?> carried = "pwsh";
        Optional<string?> cleared = null;

        await Assert.That(carried.IsSet).IsTrue();
        await Assert.That(carried.Value).IsEqualTo("pwsh");
        await Assert.That(cleared.IsSet).IsTrue();
        await Assert.That(cleared.Value).IsNull();
    }

    [Test]
    public async Task Equals_Should_Separate_Absent_From_An_Explicit_Null()
    {
        Optional<string?> absent = default;
        var cleared = Optional<string?>.Null;

        await Assert.That(absent == cleared).IsFalse();
        await Assert.That(absent != cleared).IsTrue();
        await Assert.That(absent.Equals(cleared)).IsFalse();
        await Assert.That(cleared.Equals((object)absent)).IsFalse();
    }

    [Test]
    public async Task Equals_Should_Match_Instances_Carrying_The_Same_State_And_Value()
    {
        Optional<string?> left = "pwsh";
        Optional<string?> right = "pwsh";

        await Assert.That(left == right).IsTrue();
        await Assert.That(left.Equals(right)).IsTrue();
        await Assert.That(left.Equals((object)right)).IsTrue();

        // The implicit conversion reaches overload resolution too, so the strongly typed overload
        // compares against the set instance a bare value converts to; only the boxed object
        // overload sees a foreign type.
        await Assert.That(left.Equals("pwsh")).IsTrue();
        await Assert.That(left.Equals((object)"pwsh")).IsFalse();
    }

    /// <summary>
    /// The comparison <c>WhenWritingDefault</c> performs, spelled the way the serializer spells it.
    /// </summary>
    [Test]
    public async Task EqualityComparer_Should_Treat_Only_The_Absent_Instance_As_Default()
    {
        var comparer = EqualityComparer<Optional<string?>>.Default;

        await Assert.That(comparer.Equals(default, default)).IsTrue();
        await Assert.That(comparer.Equals(default, Optional<string?>.Null)).IsFalse();
        await Assert.That(comparer.Equals(default, "pwsh")).IsFalse();
    }

    [Test]
    public async Task GetHashCode_Should_Agree_With_Equality()
    {
        Optional<string?> left = "pwsh";
        Optional<string?> right = "pwsh";

        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    [Test]
    public async Task Value_Types_Should_Carry_The_Same_Three_States()
    {
        Optional<bool?> absent = default;
        var cleared = Optional<bool?>.Null;
        Optional<bool?> carried = true;

        await Assert.That(absent.IsSet).IsFalse();
        await Assert.That(absent.Value).IsNull();
        await Assert.That(cleared.IsSet).IsTrue();
        await Assert.That(cleared.Value).IsNull();
        await Assert.That(carried.Value).IsTrue();
        await Assert.That(carried == cleared).IsFalse();
    }
}
