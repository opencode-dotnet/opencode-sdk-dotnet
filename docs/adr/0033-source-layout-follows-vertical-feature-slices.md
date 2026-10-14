# Source layout follows vertical feature slices; namespaces do not follow folders

Date: 2026-10-14

The SDK generates about fourteen kinds of file across 28 client families, and the generated layout
put each kind in its own horizontal folder: every model in one `Models/` folder of some 740
files, every response adapter in one `Internal/ResponseAdapters/`. A reader who knows a feature
could not find its client, models, internals and tests in one place. The code is maintained by
people, so the layout follows features: each family is a vertical slice that holds its client,
its operation records, its models under `Models/`, its implementation under `Internal/`, and a
mirrored test folder. Shared vocabulary has a shared home (`Common/`), shared machinery lives in
a root `Internal/`, and hosting has its own umbrella (`Hosting/`).

The decisions:

- **Namespaces do not follow folders.** Public types keep `OpenCode.Sdk` and
  `OpenCode.Sdk.Models` wherever their files live; every internal type declares
  `OpenCode.Sdk.Internal`, with no sub-namespaces. A folder move is then a rename and never a
  breaking change. A fail-closed namespace-policy test replaces IDE0130 for the SDK project.
- **One Internal rule.** A file under any `Internal/` folder is internal and declares the internal
  namespace; every other file is public. Folder segment, namespace and accessibility agree.
- **A model's name prefix names its family.** Models without a usable prefix are placed by
  reasoned curation rows, and a new unprefixed model fails generation until a row places it.
  Which families' operations reach a model is reported on every refresh and never moves a file.
- **Event arms live with the family their prefix names**, beside their data companions; a union
  kit lives with the family whose name it carries.
- **Tests mirror their subject's path** with every `Internal` segment dropped, and test
  namespaces follow test folders.
- **Generated and hand-written files share folders.** The generation manifest identifies
  generated files, and the generator marks them `linguist-generated` from it.

## Considered options

- **Namespaces that follow folders.** Rejected: every placement decision and every refresh that
  moves a model would break consumers, and a replay over past refreshes showed such moves were
  regular.
- **Ownership by reach (the families whose operations use a model).** Rejected: reach churned
  across refreshes far more than name prefixes did, and a folder chosen by reach cannot be
  predicted from a type's name.
- **A `Generated/` folder inside each slice.** Rejected: it splits a feature in two again; the
  manifest already identifies generated files.
- **All event arms in the Events family.** Rejected: it would move the Sessions family's own log
  vocabulary out of Sessions.
- **A separate test-support project.** Rejected: most helpers have one consumer slice and belong
  beside it.

## Consequences

- The migration is a one-time move, slice by slice; a half-migrated tree is a valid state.
- Body requests sit at their family root beside query requests but keep the `OpenCode.Sdk.Models`
  namespace, while query requests stay in `OpenCode.Sdk`.
- Sessions is the one family whose models split into named areas, because its model folder alone
  is too large to scan.
- The name rule places some models away from the operations that use them, such as the `Form*`
  models in Forms; the gain is that a model's folder follows from its name.
- Internal converter names in the public API text change once, when internal namespaces flatten;
  the public surface does not change.

Evidence: internal research, 2026-10-14, layout study, independent reviews and measurements.

Reversal: a namespace-grouped API reference becomes a deliverable, or the name rule starts moving
files on refreshes.
