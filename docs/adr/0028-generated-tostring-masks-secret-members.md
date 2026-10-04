# Generated ToString masks secret members

Date: 2026-09-23

A generated model is a C# record, and a record's compiler-synthesized `ToString()` prints every
public member. Logging a configuration, a request, or a union that holds one therefore wrote its
secrets: `McpOAuthConfig.ClientSecret` printed through the record and, since structural unions
print their active arm, through `McpRemoteConfigOauth`. The OpenAPI document cannot say which
members are secret — `client_secret` is `{"type": "string"}` — and upstream carries no
`format: password` or `writeOnly` marker; its runtime keeps secrets in Effect's `Redacted`, which
the document does not project. Upstream does name secrets in one place the SDK can follow: its
HTTP recorder masks the JSON fields in `DEFAULT_REDACT_JSON_FIELDS`
(`packages/http-recorder/src/redaction/redactor.ts`: `access_token`, `api_key`, `apikey`,
`client_secret`, `password`, `refresh_token`, `secret`, `token`) in every recorded body, matching a
key after dropping every character outside `[a-z0-9]` and lower-casing it, and writes
`[REDACTED]` in its place.

The generator masks secret members in two layers. The floor is the recorder's list with its own
matching rule: a model member whose wire name matches prints `[REDACTED]` when it holds a value and
prints empty when it does not, so presence stays visible. Judgement rides curation rows
(`redactedMembers`, keyed by model and wire name, with a reason): `redact: true` masks a member the
floor misses, `redact: false` lifts a floor mask. Beside the floor stands a fail-closed wall: a
member whose wire name contains one of the words upstream's recorder reads as a secret marker in
an environment-variable name (`ENV_SECRET_NAMES` in `secrets.ts`: API, AUTH, BEARER, CREDENTIAL,
KEY, PASSWORD, SECRET, TOKEN, case-insensitive, anywhere) refuses the bind until a row decides it
— a `redactedMembers` row, or a `secretLookingNames` row keyed by wire name that states the value
is never a credential. Every row must still decide something against the bound models, or the
bind refuses it, as every other curation row does. Both upstream files are source-watched, so a
change to the list, the rule, or the marker words is reviewed at refresh.

A model with a masked member overrides `ToString()` and prints through the runtime's
`RecordPrinter` in the compiler's own shape — every public member in declaration order, the same
spacing, an absent value empty — so the masked value is the only difference. It overrides
`ToString()` rather than declaring `PrintMembers` because a sealed record with no record base may
declare that hook only privately (CS8879), where IDE0051 cannot see the synthesized caller and the
generator's format pass strips it; structural unions override `ToString()` for the same reason.

## Considered options

- A name heuristic alone, with no upstream floor — rejected: the heuristic is the wall, not the
  mask; `tokens` (usage counts) and `key` (form-field identifiers) are not secrets.
- Generated `ToString()` printing no values at all — rejected: every model loses a useful
  diagnostic to protect a handful of members.
- A `Redacted<string>` wrapper type on secret members — rejected: a breaking change to the public
  model surface and to serialization for a printing concern.
- Waiting for upstream to project `Schema.Redacted` as `format: password` or `writeOnly` — kept as
  the reversal trigger below, not as the mechanism; the SDK cannot ship the leak meanwhile.

## Consequences

- At the pin the floor masks `McpOAuthConfig.ClientSecret`; rows mask the integration key a key
  authentication method stores (`IntegrationConnectKeyRequest.key`) and every user-configured
  header and environment map: the MCP server maps, the provider and model header maps of both the
  provider catalog and the configuration document, the configuration's agent-request headers and
  formatter and LSP-server environments, the terminal-create environments, and the session
  environment. A record prints a dictionary's type name today, so those map rows change the
  printed text only from the map's type name to `[REDACTED]`; they keep the value out if printing
  ever changes.
- A union whose known arms reach a masked member at any depth — through object members, list
  elements, dictionary values, nested unions, and structural-union arms — prints the payload its
  unknown arm preserves as `[REDACTED]` and keeps that arm's marker visible, since an arm the pin
  does not know may carry the same secret. At the pin that is `UnknownCredentialValue`,
  `UnknownMcp`, and `UnknownConfigEntry` (a configuration document reaches masked maps through its
  MCP servers, providers, agents, formatters, and LSP servers), and the Unknown arm of the structural unions `McpRemoteConfigOauth`, `ConfigInfoFormatter`,
  `ConfigInfoLsp`, and `ConfigLspEntry`.
- Masking is a printing concern only: equality, serialization, and the members' values are
  unchanged.
- A refresh that adds a secret-shaped member fails generation until a row decides it; the
  pending-operation probe sets that wall aside, since a missing row is curation, not a shape wall.
- Response envelopes are hand-written records outside this mechanism; the raw error body they
  print is tracked separately (issue #100).
- Reversal: upstream projects a secret marker into the OpenAPI document (for example
  `format: password` or `writeOnly` from Effect's `Schema.Redacted`); the marker then becomes the
  floor and the recorder list a cross-check.
