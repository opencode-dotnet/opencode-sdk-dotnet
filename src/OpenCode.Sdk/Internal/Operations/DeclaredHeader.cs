namespace OpenCode.Sdk.Internal;

/// <summary>
/// One request header the pinned document declares as an operation parameter, carried from the
/// generated internal-raw method that collected it to the decoration policy that writes it. It
/// is not a general header facility: only code inside this assembly — generated internal-raw
/// methods and the hand-written family doors delegating to them — can reach the channel, and
/// only a document-declared parameter ever feeds one (ADR-0013, ADR-0021).
/// </summary>
/// <param name="Name">The header's wire name, exactly as the document spells it.</param>
/// <param name="Value">The header's value; an omitted header contributes no entry at all.</param>
internal readonly record struct DeclaredHeader(string Name, string Value);
