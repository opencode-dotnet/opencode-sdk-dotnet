using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Classifies a query parameter the document projects as a plain string. A
/// <c>queryParameterTypes</c> row binds it as the integer upstream decodes; a
/// <c>stringQueryParameters</c> row keeps it a string; a parameter with neither row refuses, so a
/// refresh that brings a new one stops until it is classified (ADR-0037).
/// </summary>
internal static class QueryParameterTypePolicy
{
    /// <summary>The C# keyword a row names for a 32-bit integer.</summary>
    public const string Int32Keyword = "int";

    /// <summary>The C# keyword a row names for a 64-bit integer.</summary>
    public const string Int64Keyword = "long";

    /// <summary>The refusal a plain-string query parameter without a classification row raises.</summary>
    public const string UnclassifiedProblem =
        "the query parameter is a plain string that no curation row classifies: add a queryParameterTypes row when upstream "
        + "decodes it as a number, or a stringQueryParameters row that says why it stays a string";

    /// <summary>Gets the query kind a row's C# keyword binds, or <see langword="null"/> for any other keyword.</summary>
    public static QueryValueKind? KindOf(string dotnetType) => dotnetType switch
    {
        Int32Keyword => QueryValueKind.Int32,
        Int64Keyword => QueryValueKind.Int64,
        _ => null,
    };

    /// <summary>
    /// Gets the kind a plain-string query parameter binds as, or <see langword="null"/> after
    /// refusing a parameter no row classifies.
    /// </summary>
    public static QueryValueKind? Classify(OperationFacetContext context, string parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameter);

        var operationId = context.Operation.OperationId;
        var typed = context.Curation.QueryParameterTypes.FirstOrDefault(row => Matches(row.OperationId, row.Parameter, operationId, parameter));
        if (typed is not null)
        {
            // The validator refuses an unknown keyword by name; nothing binds under it meanwhile.
            return KindOf(typed.DotNetType);
        }

        if (context.Curation.StringQueryParameters.Any(row => Matches(row.OperationId, row.Parameter, operationId, parameter)))
        {
            return QueryValueKind.Text;
        }

        context.Errors.Add(BindingErrorCategory.Curation, operationId + ":" + parameter, UnclassifiedProblem);
        return null;
    }

    private static bool Matches(string rowOperationId, string rowParameter, string operationId, string parameter) =>
        string.Equals(rowOperationId, operationId, StringComparison.Ordinal)
        && string.Equals(rowParameter, parameter, StringComparison.Ordinal);
}
