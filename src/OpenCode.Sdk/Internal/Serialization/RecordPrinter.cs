using System.Text;

namespace OpenCode.Sdk.Internal.Serialization;

/// <summary>
/// Formats a generated model that carries a secret member, and the unknown arm of a union whose
/// known arms reach one, for its record's <c>ToString()</c> (ADR-0028). The output keeps the compiler-synthesized shape,
/// <c>McpOAuthConfig { ClientId = app, ClientSecret = [REDACTED], Scope =  }</c>: each member's
/// own string form, an absent value printed empty. A masked member prints upstream's own marker
/// when it holds a value and stays empty when it does not, so its presence is still visible.
/// </summary>
internal static class RecordPrinter
{
    /// <summary>The marker upstream's HTTP recorder writes in place of a redacted value.</summary>
    public const string Redacted = "[REDACTED]";

    /// <summary>Masks a present value.</summary>
    /// <typeparam name="T">The member's type.</typeparam>
    /// <param name="value">The member's value.</param>
    /// <returns><see cref="Redacted"/>, or null when there is no value.</returns>
    public static string? Redact<T>(T value) => value is null ? null : Redacted;

    /// <summary>Renders <c>&lt;typeName&gt; { &lt;name&gt; = &lt;value&gt;, ... }</c>.</summary>
    /// <param name="typeName">The record's type name.</param>
    /// <param name="members">Every public member in declaration order, masked values already masked.</param>
    /// <returns>The printed record.</returns>
    public static string Format(string typeName, params (string Name, object? Value)[] members)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(members);

        // The compiler's own spacing: "Type { A = 1, B = 2 }", and "Type { }" with no members.
        var builder = new StringBuilder(typeName).Append(" { ");
        for (var index = 0; index < members.Length; index++)
        {
            _ = builder.Append(index == 0 ? string.Empty : ", ").Append(members[index].Name).Append(" = ").Append(members[index].Value);
        }

        return builder.Append(members.Length == 0 ? "}" : " }").ToString();
    }
}
