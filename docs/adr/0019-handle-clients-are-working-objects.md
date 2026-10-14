# Handle clients exist only for working objects

Date: 2026-08-25

Status: accepted

A family's curation row places its per-id operations in one of two shapes: a handle client
(`Sessions.GetSessionClient(id)` — the id bound once, operations chained on the instance) or
flat methods taking the id as the leading argument (`Agents.GetAgentAsync(id)`). The binder
supports both; nothing but the curation row decides. Decision: **a family gets a handle client
only when its per-id operations form a working object** — several operations that naturally
chain on the same id, or a live stateful lifecycle. Single-shot lookups and admin actions take
the id as a method argument; a handle there is pure ceremony that moves the id away from the
call and adds an object per invocation. Under this rule sessions, shells, ptys, and integrations
keep handles; agents, credentials, saved permissions, and providers are flat. Each family's
curation row records its placement and the reason.

**The judgment runs against the family's complete pinned surface, never the currently selected
slice.** The selection profile admits operations in batches, so the visible slice understates a
family mid-series and placement would flip between batches; the near-miss that motivated this
rule was exactly that — a family whose selected slice showed one per-id operation was almost
flattened while its full pinned surface carried a connect/disconnect lifecycle across four,
which is handle territory. Placement is decided when the family is first admitted and revisited
only at a sanctioned spec refresh, which may also move operations between families.

The rule and the emitted shape align with the [Azure SDK .NET guidelines](https://azure.github.io/azure-sdk/dotnet_introduction.html):
subclients exist to "group operations related to a service resource or functional area to
improve API usability" (`dotnet-use-subclients`), factory methods create them
(`dotnet-subclient-factory-methods`) and "take a resource identifier as a parameter"
(`dotnet-subclient-factory-methods-parameters`), and a subclient carries no public
constructor (`dotnet-subclient-no-constructor`) while keeping a protected parameterless one
for mocking (`dotnet-subclient-mocking`) — all shapes the generated handle clients already
emit, alongside the guidelines' virtual service methods, virtual client accessors, and
`Get<client>Client()` factory naming. The single guideline not yet adopted is
`dotnet-subclient-properties` ("YOU SHOULD expose resource identifiers as properties on the
resource client"): the handle clients hold the id privately today, and surfacing it is
additive.

Consequences: placement changes are free until the public surface freezes before `1.0.0`;
after the freeze a placement change is breaking, so a refresh that reshapes a family's per-id
surface forces the decision to the next major version — that is this record's reversal
trigger. Adding a handle to a flat family later is additive and safe; removing one is not.
Whether flat families should additionally expose parent-mediated id access as a convenience is
deliberately parked, to be evaluated in the API review before the freeze.
