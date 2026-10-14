using System.Globalization;

namespace OpenCode.Sdk.Internal;

/// <summary>The one author of the failures a status verdict raises on any plane.</summary>
internal static class StatusVerdictFailures
{
    /// <summary>Builds the protocol failure for a success status the operation does not declare.</summary>
    public static OpenCodeTransportException UndeclaredSuccess(int status) =>
        new($"The opencode API returned undeclared success status {status.ToString(CultureInfo.InvariantCulture)}.");
}
