using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Composes the query-string suffix of a generated route; unset optionals are omitted and
/// every value is escaped. Route builders own the wire conversion rules, so refusals here
/// are the route-boundary contract.
/// </summary>
internal sealed class QueryStringBuilder
{
    private StringBuilder? _builder;

    public string Value => _builder?.ToString() ?? string.Empty;

    public void AddText(string name, string? value)
    {
        if (value is not null)
        {
            Append(name, value);
        }
    }

    public void AddOrder(string name, ListOrder? value)
    {
        if (value is null)
        {
            return;
        }

        var wire = value switch
        {
            ListOrder.Ascending => "asc",
            ListOrder.Descending => "desc",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown ListOrder value."),
        };
        Append(name, wire);
    }

    public void AddBoolean(string name, QueryBoolean? value)
    {
        if (value is null)
        {
            return;
        }

        var wire = value switch
        {
            QueryBoolean.True => "true",
            QueryBoolean.False => "false",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown QueryBoolean value."),
        };
        Append(name, wire);
    }

    public void AddParentFilter(string name, SessionParentFilter? value)
    {
        if (value is not null)
        {
            Append(name, value.WireValue);
        }
    }

    public void AddLocation(string name, LocationSelector? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Directory is not null)
        {
            AppendBracketed(name, "directory", value.Directory);
        }
    }

    private void Append(string name, string value)
    {
        _builder ??= new StringBuilder();
        _ = _builder.Append(_builder.Length is 0 ? '?' : '&')
            .Append(RouteValuePolicy.EscapeName(name))
            .Append('=')
            .Append(RouteValuePolicy.Escape(value, name));
    }

    /// <summary>
    /// DeepObject keys ride the wire with literal brackets, matching the first-party
    /// client's serialization; the member names are SDK literals, never caller input.
    /// </summary>
    private void AppendBracketed(string name, string member, string value)
    {
        _builder ??= new StringBuilder();
        _ = _builder.Append(_builder.Length is 0 ? '?' : '&')
            .Append(RouteValuePolicy.EscapeName(name))
            .Append('[')
            .Append(member)
            .Append(']')
            .Append('=')
            .Append(RouteValuePolicy.Escape(value, name));
    }
}
