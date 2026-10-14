using System.Text;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The persistent-terminal handoff route's answer with the ticket kept as the route wrote it: the
/// pinned client parses the body with a plain <c>JSON.parse</c> and publishes <c>body.handoff</c>
/// whole (<c>prepare</c> in <c>pty-handoff.ts</c>), and the replacement daemon decodes it with its own schema,
/// so a member this pin does not model must still reach it. The typed door would re-serialize the
/// ticket through the pinned four-member model and drop such a member.
/// </summary>
internal sealed record ServiceHandoffTicketResponse : OpenCodeResponse
{
    /// <summary>Gets a value indicating whether the body is an object with a <c>handoff</c> member.</summary>
    public bool HasHandoffMember { get; init; }

    /// <summary>Gets the ticket exactly as the route wrote it, or null for a JSON null or an absent member.</summary>
    public JsonElement? Handoff { get; init; }

    /// <summary>Prints the status metadata only: the ticket is secret-bearing.</summary>
    /// <param name="builder">The builder the record's <see cref="object.ToString"/> fills.</param>
    /// <returns>Whether anything was printed.</returns>
    protected override bool PrintMembers(StringBuilder builder) => base.PrintMembers(builder);
}
