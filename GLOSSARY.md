# opencode SDK for .NET

Typed .NET client for the opencode HTTP API; an MCP server built on it lives in a sibling
repository. Its domain is upstream's, opencode's concepts seen through an SDK lens, beside the
language this project uses for its own engineering.

## Language

### Upstream domain

#### Sessions

**Session**:
A persistent conversation with an Agent, and the API's central concept.

**Session metadata**:
Host-supplied annotations on a Session: arbitrary keys with JSON values, opaque to opencode and
durable from creation. Children and forks inherit the parent's unless their creator supplies its
own.

**Message**:
One entry in a Session's projected history, of one kind: user, assistant, synthetic, system, skill,
shell, compaction, idle, or an agent, model or location selection record. Only an assistant Message
carries content.

**Assistant content**:
A typed fragment of an assistant Message: text, reasoning, or a tool call.
_Avoid_: part (the retired 1.x term)

**Prompt**:
The input a user gives a Session: text, plus optional file, Agent and skill attachments.

**Turn**:
Every Step a Session takes from one idle point to the next, including Prompts steered in while it
was busy; an idle Message closes it and records its outcome. A server shutdown does not close a
Turn, because the resumed run continues it.
_Avoid_: Execution (upstream's word for one run of the Session; a restart starts another, so a Turn
can span several)

**Step**:
One logical unit of model work within a Turn, which may take several attempts before it ends or
fails.

**Session inbox**:
The queue of pending work for one Session: a Prompt, a synthetic message, a Compaction, or a move,
each delivered by Steer or Queue.

**Steer / Queue**:
The two delivery choices for a Session inbox item. A steered item joins the busy Session at its next
Step boundary, inside the current Turn; a queued item waits for the idle point and starts the next
Turn.

**Compaction**:
The summarizing of a Session's history, recorded as a Message of its own kind that runs, completes
or fails. A completed Compaction stands in for the history before it in what the model sees: its
summary, or a provider's replacement window, which replays only on the model that made it.

#### Events

**Event**:
Something that happened: an identified, typed record carrying its own data and, optionally, the
Location it happened in. Every Event is either durable or ephemeral.

**Durable / ephemeral Event**:
The two durability classes of an Event. A durable Event carries an envelope naming its Aggregate,
its sequence in that Aggregate's log and the schema version that committed it, so it can be read
back; an ephemeral Event reaches only the subscribers attached at the time.

**Aggregate**:
The unit a durable Event log is kept per: an id taken from the Event's own data, with a monotonic
sequence over the Events committed against it. Session Events aggregate on their Session, others
elsewhere, such as worktree resolution on its Project.

**Durable Session event stream**:
One Session Aggregate's durable Event log, read back from an exclusive sequence and optionally
followed into live Events, with one sync marker between replay and live. It holds only what the
server persisted, and persistence is a server option that is off by default.
_Avoid_: session events (ambiguous with the Live event stream)

**Live event stream**:
The server's instance-wide stream of Events, each connection opening with a `server.connected`
Event. It has no cursor or replay: a slow reader overflows, and Events during a disconnection are
lost.

**Shared event feed**:
Upstream's client-side fan-out of one Live event stream connection to every subscriber of a client,
each with its own Event filter and its own bounded queue; a subscriber that falls behind overflows
and ends alone while the others continue, and every subscriber ends when the connection does.
Reporting the connection's state and reconnecting without end belong to upstream's UI connection
layer, which resubscribes around the feed.

**Admission**:
The point at which a subscriber joins a Shared event feed, marked by the first `server.connected` it
receives (the cached one when the feed is already connected); from then on it is offered every Event
it selects that the feed fans out, and none fanned out before.
_Avoid_: connection (a new subscriber's marker does not mean the wire reconnected); admitting an
upstream operation (the generator's selection of the protocol surface, a different thing)

#### Capabilities and catalogs

**Permission**:
The gate on agent actions: rules matching an action and a resource to allow, deny or ask, where an
ask raises a request the client answers and an answer can become a saved, Project-scoped grant.
Agents and Sessions each carry a ruleset; a Session's is evaluated after its Agent's, and the last
matching rule wins.

**Form**:
A structured question the server raises against a Session or a Location and a client answers: typed
fields with optional visibility conditions, ending answered or cancelled.

**Integration**:
A connectable upstream service and the authentication methods it offers: an API key, OAuth, a
command, the environment, or credentials managed outside opencode. Connecting leaves a connection on
it; the OAuth and command methods get there through an attempt with its own status.

**Provider**:
An LLM provider entry in its own catalog, with its activation state and request settings, naming the
Integration it is connected through. Its Models form a separate catalog.

**Model**:
One entry in the model catalog, naming its Provider and carrying capabilities, variants, costs and
limits, with two ids: its catalog id, which Sessions, Agents and Messages reference with the
Provider and an optional variant, and its provider model id, which the Provider's own API uses. The
SDK names upstream's `model` client `LanguageModelsClient`, because the `Models` namespace belongs
to the generated model layer.
_Avoid_: model id alone (it is ambiguous between the two)

**Agent**:
A configured opencode persona, `build` by default and others from config or plugins, selectable per
Session or attachable to a single Prompt. It carries its own Permission ruleset, an optional Model
and a step budget.
_Avoid_: mode (an Agent's mode is its primary or subagent eligibility, not its persona)

**Plugin activation**:
The asynchronous per-Location settling of a server's plugins, during which Providers register and
the model catalog fills. Only the endpoints that list or use Providers wait for it, for a bounded
time; any other answer, a server info answer among them, can come before it settles.
_Avoid_: readiness (the Launcher's start signal, a different thing)

**Plugin RPC**:
A method surface a server-side plugin registers under an RPC id, called at a Location with untyped
JSON input and output; the plugin's notifications arrive on the Live event stream as `rpc.*` Events.
_Avoid_: RPC alone (the SDK's own HTTP calls are not RPC)

#### Servers and addressing

**Server process**:
One running `opencode serve` process at one endpoint, which the API is bound to. It owns the
process-global state: the auth store, the event streams and log, Sessions, Projects and saved
Permissions, but not configuration, which belongs to the Instance.

**Location**:
The absolute directory a public HTTP request is addressed to; resolving one yields its Project.

**Project**:
What a Location resolves to: its version-control repository, or the directory itself when it has
none, with its own id and canonical path. Sessions, saved Permissions and Worktrees are scoped to
it.

**Worktree**:
A managed local directory in a Project's worktree inventory, created, listed, removed and refreshed
against that Project's id.

**Worktree strategy**:
One of the server's registered, open-ended ways to discover and create Worktrees. The Project
selects one for creation, and removal uses the one recorded for the Worktree.

**Workspace**:
An upstream core addressing concept retained in some event and log contracts. The public HTTP API
offers neither workspace lifecycle operations nor workspace targeting.

**Instance**:
One Location's working context inside a Server process: its configuration, agents, tools and MCP
servers. A Server process hosts many, one per Location.

**Directory targeting**:
Choosing a request's Location per request: an explicit directory query value first, then the
directory header, then the server's own working directory.

#### Terminals

**PTY**:
A pseudo-terminal session managed through the API.

**PTY connection**:
The live WebSocket attached to one PTY, carrying replayed and live output out and input in. The PTY
outlives any connection to it.
_Avoid_: terminal (the PTY itself), session (the SDK's PTY session works over a connection)

**Replay cursor**:
The absolute position in a PTY's retained output from which a connection replays. A normal PTY can
also attach live-only; a Persistent PTY always replays, from the oldest retained byte by default.

**Connect ticket**:
The short-lived, single-use credential minted for handing a PTY connection to a browser.

**Persistent PTY**:
A terminal owned by the opencode PTY daemon rather than by the Server process, keyed to a Session.
It survives a server restart through a Handoff.

**Handoff**:
The expiring, single-use ticket that passes ownership of the PTY daemon from an outgoing Server
process to its replacement, which claims it before it becomes ready. The instance id it carries
names the daemon process, not an Instance.

**Attachment**:
One live connection to a Persistent PTY: its identity, its role, the resize generation it attached
at, and the replay bounds the server granted it.
_Avoid_: attachment unqualified (a Prompt's file, agent and skill attachments are a different thing)

**Controller / Observer**:
The two roles of an Attachment: the Controller's input and viewport drive the terminal, and an
Observer only reads. Input sent on an Observer's connection is accepted and makes that Attachment
the Controller, and the previous Controller becomes an Observer.

**Current terminal**:
A Session's most recently controlled Persistent PTY, which a terminal read reports; observing does
not select one. The selection is server-local and resets when the server restarts.

**Takeover**:
A Controller attach that takes control from the Persistent PTY's current Controller, which becomes
an Observer. Without it, a Controller attach fails while another Attachment controls the terminal.

**Checkpoint**:
The terminal-escape byte stream that repaints a screen state.

**Framed input**:
The Persistent PTY input protocol in which every input or viewport message carries the viewport it
was typed at.

### This project's language

#### Connection modes

**Launcher**:
The in-core component that starts, monitors and stops a local `opencode serve` process for the
Standalone server mode.

**Standalone server**:
A fresh private `opencode serve` child that the SDK caller owns, upstream's standalone connection
mode.

**Background service**:
Upstream's registered daemon connection mode, which the SDK discovers, ensures and stops through a
Registration file.

**Registration file**:
The on-disk record a Background service publishes, with its address, credential and instance
identity, so clients can discover it. Its identity is its id, version, URL and pid, never the
password.

**Process identity**:
A process's pid together with the start time the operating system recorded for it, so a pid reused
by an unrelated process after the SDK first observed the process is not mistaken for it. It names a
process; the Registration file's identity names a service.

**Registered server handle**:
A server handle produced by discovery or Ensure that does not own the process; disposing it never
stops the shared service.
_Avoid_: guarded server handle, shared handle (draft names for the same thing)

**Service channel**:
The CLI's compiled release identity that selects the registration and service-config filenames and
the legacy-migration rules. The SDK has none compiled in: a caller may name one.

#### Snapshot and refresh

**Protocol surface**:
The pinned protocol API document, the surface this SDK generates; its public names carry no
protocol-version prefix.
_Avoid_: v2, V2 (in public naming); legacy (the retired 1.x dual-surface vocabulary); modern surface
(its name in dated research)

**Accepted snapshot**:
The reviewed protocol identity the SDK builds against: an exact upstream commit, the committed
document, the recipe that produced it, and the digests that prove them.
_Avoid_: spec pin (retired term)

**Snapshot recipe**:
The ordered procedure that produces the accepted document from the exact upstream commit: upstream's
own pinned generator, run over a patch list that is normally empty.

**Snapshot receipt**:
The immutable record of one prepared snapshot candidate, with its inputs, hashes, patches and
invariants, that a human reviews before it is applied.

**Restore patch**:
A temporary, hash-verified snapshot patch that recovers contract content upstream's projection lost,
carrying an upstream report and a retirement predicate.

**Source watch**:
The pinned set of upstream files that hand-written code in this family depends on, each recorded by
path and hash with one or more behaviours, each naming its owning product. It is evidence a refresh
reviews and a curated type's evidence must be in; it never feeds generated output.

#### Generation

**Contract inventory**:
The complete operation set of the accepted document, each operation selected, pending, declined or
transport-owned.

**Target surface**:
The operations required to be callable; by default the whole Contract inventory.

**Transport-owned operation**:
An operation whose transport the HTTP pipeline cannot carry, such as a WebSocket upgrade,
inventoried by the generator and callable only through a hand-written door.

**Fingerprint pin**:
The committed hash of an excluded operation's own spec subtree; CI breaks when that construct
drifts.

**Curation config**:
The generator's declarative, fail-closed input that maps spec constructs to public names and rules;
an unmapped construct breaks generation.

**Operation-identity row**:
A reason-bearing curation row that admits and names an operation whose upstream identity violates
upstream's own conventions, retiring when upstream fixes it.

**Naming grammar**:
The closed set of identifier segments that name an operation's verb when they close its identity;
the HTTP method is never a name source.

**Operation-name row**:
A reason-bearing curation row naming an operation the Naming grammar cannot name, or names
gracelessly.

**Enum member-name row**:
A reason-bearing curation row naming the C# member one enum value becomes where the mechanical
casing is graceless.

**Parameter-name row**:
A reason-bearing curation row naming the C# parameter one route value becomes where the mechanical
name says less than the rest of the surface; the wire name still fills the route.

**Stabilize-duplicate collapse**:
The mechanical fold of a duplicate-named component into its base name when the two are structurally
identical, recorded as an implicit alias rather than a curated one.

**Output manifest**:
The committed inventory of generated files, the only place generated-ness is tracked.

**Model layer**:
The generated types and serializer registry shipped inside the SDK package.

**Hoisted member**:
A property a generated union interface declares because every member of the union declares it in the
same shape; it is never the discriminator. Where members promote their own records for that shape,
its type is a generated carrier interface the records implement.
_Avoid_: shared member (ambiguous with a property two unrelated schemas happen to share)

**Prefix-tagged arm**:
A tagged union's single branch, at most one per union, whose discriminator is a string prefix rather
than a literal, such as the `rpc.*` Events on the Live event stream.
_Avoid_: pattern arm (a general regex is never admitted); catch-all (the Unknown variant carrier is
the catch-all)

**Unknown variant carrier**:
The per-union variant that keeps the tag and raw payload of an unrecognized discriminator, or, on the
live and durable event streams, of a known variant that cannot be read, when the carrier can hold
that payload.

**Recorded tolerance**:
An explicit, registered runtime exception to the fail-closed default.

#### Client runtime

**Behavior core**:
The hand-written transport core where all request behavior lives; generated methods only delegate to
it.

**Envelope**:
The generated, typed response object of one operation, carrying its status and error state and its
named payload properties.
_Avoid_: envelope alone (a durable Event's envelope is a different thing)

**Single-key envelope**:
A success body declared inline as an object requiring exactly one property other than `data`, whose
payload the Envelope carries under that key's PascalCase name without a wrapper model; a named
component with one property keeps its own identity.

**Bound handle**:
A sub-client bound to one resource id, such as a Session, over the shared pipeline; it never caches
server state.

**Cursor sequence**:
What a generated enumerate method returns: the items of every page in order, with the same
traversal's pages beside them, issuing its own requests on each enumeration.

**PTY session**:
The working object over one PTY connection, which reads frames, writes input, and closes on
disposal.
_Avoid_: session alone (an opencode Session is a conversation)

**PTY frame**:
One message read from a PTY connection: output text, or the single cursor control frame the server
sends once the retained buffer has been replayed.
