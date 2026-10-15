using System.Globalization;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Operations;

public sealed class QueryStringBuilderTests
{
    [Test]
    public async Task Value_Should_Be_Empty_When_Nothing_Was_Added()
    {
        var query = new QueryStringBuilder();

        await Assert.That(query.Value).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AddText_Should_Skip_Null_Values()
    {
        var query = new QueryStringBuilder();

        query.AddText("search", null);

        await Assert.That(query.Value).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AddText_Should_Join_Values_With_Query_Separators()
    {
        var query = new QueryStringBuilder();

        query.AddText("search", "alpha");
        query.AddText("cursor", "beta");

        await Assert.That(query.Value).IsEqualTo("?search=alpha&cursor=beta");
    }

    [Test]
    public async Task AddText_Should_Escape_The_Value()
    {
        var query = new QueryStringBuilder();

        query.AddText("search", "a b&c=d");

        await Assert.That(query.Value).IsEqualTo("?search=a%20b%26c%3Dd");
    }

    [Test]
    public async Task AddText_Should_Escape_The_Name()
    {
        var query = new QueryStringBuilder();

        query.AddText("odd name&x", "v");

        await Assert.That(query.Value).IsEqualTo("?odd%20name%26x=v");
    }

    [Test]
    public async Task AddText_Should_Refuse_A_Lone_Surrogate()
    {
        var query = new QueryStringBuilder();

        var exception = Assert.Throws<ArgumentException>(() => query.AddText("search", "\ud800"));

        await Assert.That(exception.ParamName).IsEqualTo("search");
    }

    [Test]
    public async Task AddText_Should_Accept_The_Downlevel_Length_Limit()
    {
        var query = new QueryStringBuilder();
        var value = new string('a', 32_766);

        query.AddText("search", value);

        await Assert.That(query.Value).IsEqualTo("?search=" + value);
    }

    [Test]
    [Arguments(ListOrder.Ascending, "?order=asc")]
    [Arguments(ListOrder.Descending, "?order=desc")]
    public async Task AddOrder_Should_Write_The_Wire_Spelling(ListOrder order, string expected)
    {
        var query = new QueryStringBuilder();

        query.AddOrder("order", order);

        await Assert.That(query.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task AddOrder_Should_Refuse_An_Undefined_Value()
    {
        var query = new QueryStringBuilder();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => query.AddOrder("order", (ListOrder)7));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments(QueryBoolean.True, "?follow=true")]
    [Arguments(QueryBoolean.False, "?follow=false")]
    public async Task AddBoolean_Should_Write_The_Declared_String_Token(QueryBoolean value, string expected)
    {
        var query = new QueryStringBuilder();

        query.AddBoolean("follow", value);

        await Assert.That(query.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task AddBoolean_Should_Refuse_An_Undefined_Value()
    {
        var query = new QueryStringBuilder();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => query.AddBoolean("follow", (QueryBoolean)7));
        await Task.CompletedTask;
    }

    [Test]
    public async Task AddParentFilter_Should_Write_The_Wire_Value()
    {
        var query = new QueryStringBuilder();

        query.AddParentFilter("parentID", SessionParentFilter.RootOnly);
        query.AddParentFilter("other", SessionParentFilter.Of("ses_1"));

        await Assert.That(query.Value).IsEqualTo("?parentID=null&other=ses_1");
    }

    [Test]
    public async Task AddInteger_Should_Write_The_Decimal_Text_Of_Any_Long()
    {
        var query = new QueryStringBuilder();

        query.AddInteger("limit", 25);
        query.AddInteger("cursor", long.MaxValue);
        query.AddInteger("after", -1);

        await Assert.That(query.Value).IsEqualTo("?limit=25&cursor=9223372036854775807&after=-1");
    }

    /// <summary>
    /// Upstream's client writes a number with JavaScript's <c>String(value)</c>, which no locale
    /// changes; a caller's culture with another minus sign must not reach the wire either.
    /// </summary>
    [Test]
    public async Task AddInteger_Should_Ignore_The_Current_Culture()
    {
        var culture = CultureInfo.CurrentCulture;
        var tilde = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        tilde.NumberFormat.NegativeSign = "~";
        CultureInfo.CurrentCulture = tilde;
        try
        {
            var query = new QueryStringBuilder();

            query.AddInteger("after", -42);

            await Assert.That(query.Value).IsEqualTo("?after=-42");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Test]
    public async Task Add_Should_Skip_Every_Null_Optional()
    {
        var query = new QueryStringBuilder();

        query.AddInteger("limit", null);
        query.AddText("search", null);
        query.AddOrder("order", null);
        query.AddBoolean("follow", null);
        query.AddParentFilter("parentID", null);
        query.AddLocation("location", null);

        await Assert.That(query.Value).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AddLocation_Should_Write_The_Directory_With_Literal_Brackets()
    {
        var query = new QueryStringBuilder();

        query.AddLocation("location", new LocationSelector { Directory = "/repo" });

        await Assert.That(query.Value).IsEqualTo("?location[directory]=%2Frepo");
    }

    [Test]
    public async Task AddLocation_Should_Write_Nothing_For_An_Empty_Selector()
    {
        var query = new QueryStringBuilder();

        query.AddLocation("location", new LocationSelector());

        await Assert.That(query.Value).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AddLocation_Should_Escape_The_Member_Values()
    {
        var query = new QueryStringBuilder();

        query.AddLocation("location", new LocationSelector { Directory = "a b&c=d" });

        await Assert.That(query.Value).IsEqualTo("?location[directory]=a%20b%26c%3Dd");
    }
}
