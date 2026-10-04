using System.Text.Json;

namespace OpenCode.Sdk.Internal.BackgroundService.Registration;

/// <summary>
/// The one decode step the files the CLI writes go through: a UTF-8 byte-order mark is skipped, as
/// the CLI's own decoder skips it (a hand-edited service config may carry one), a repeated member
/// is refused rather than resolved last-wins, and every document that is not JSON, or holds a
/// string that is not valid UTF-8, is absent state rather than a failure.
/// </summary>
internal static class StrictJson
{
    private const double MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false };

    /// <summary>Parses a document and reads it through the shape the caller owns.</summary>
    /// <typeparam name="T">What the caller reads out.</typeparam>
    /// <param name="utf8">The file's bytes.</param>
    /// <param name="read">The caller's shape; it returns null for a document it does not accept.</param>
    /// <returns>What the shape read, or null for a document it did not accept or could not decode.</returns>
    public static T? TryRead<T>(ReadOnlySpan<byte> utf8, Func<JsonElement, T?> read)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);

        if (utf8.StartsWith("﻿"u8))
        {
            utf8 = utf8["﻿"u8.Length..];
        }

        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), Options);
            return read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Not JSON, a repeated member, or a string that is not valid UTF-8 (which JsonDocument
            // reports only when the shape decodes it): not a document the CLI wrote either way.
            return null;
        }
    }

    /// <summary>
    /// Reads a number the CLI's schema calls an integer: <c>Number.isSafeInteger</c> over the value
    /// <c>JSON.parse</c> produced. Both parsers round a JSON number to the nearest double the same
    /// way, so <c>8080</c>, <c>8080.0</c>, and <c>8.08e3</c> are the same integer, while a fraction or a
    /// magnitude past 2^53 - 1 is not one.
    /// </summary>
    /// <param name="element">The member's value.</param>
    /// <param name="value">The integer, or zero when the value is not one.</param>
    /// <returns>True when the value is a JSON number holding a safe integer.</returns>
    public static bool TryGetSafeInteger(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var number)
            || double.IsNaN(number)
            || double.IsInfinity(number)
            || Math.Abs(number) > MaxSafeInteger)
        {
            return false;
        }

        // Number.isSafeInteger is an exact test, so this one is too: a tolerance would admit
        // 8080.0000001, which the CLI's schema refuses. The contract wins over S1244.
#pragma warning disable S1244
        if (Math.Floor(number) != number)
#pragma warning restore S1244
        {
            return false;
        }

        value = (long)number;
        return true;
    }

    /// <summary>
    /// Reads a process id the CLI's schema calls an integer. Every pid an operating system the SDK runs
    /// on issues fits an <see cref="int"/>, the type <c>Process.Id</c> and <c>kill(2)</c> take.
    /// </summary>
    /// <param name="element">The member's value.</param>
    /// <param name="processId">The pid, or zero when the value is not one.</param>
    /// <returns>True when the value is a safe integer within the <see cref="int"/> range.</returns>
    public static bool TryGetProcessId(JsonElement element, out int processId)
    {
        processId = 0;
        if (!TryGetSafeInteger(element, out var value) || value is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        processId = (int)value;
        return true;
    }

    /// <summary>Reads an optional string member: absent is null, a string is its value, anything else refuses the document.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The member name.</param>
    /// <param name="value">The value, or null when the member is absent.</param>
    /// <returns>False when the member is present but not a string.</returns>
    public static bool TryGetOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var member))
        {
            return true;
        }

        if (member.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = member.GetString();
        return true;
    }
}
