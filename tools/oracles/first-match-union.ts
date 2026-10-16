// The parity oracle for the structural union first-match arm (ADR-0016).
//
// It decodes every value of tests/OpenCode.Sdk.Tests/Serialization/Fixtures/first-match-corpus.json with
// upstream's own schemas and upstream's own Effect, then writes which union member took each value
// to first-match-expected.json beside it. The SDK's parity test asserts that the generated
// converters pick the same member. Re-run it when a refresh moves the pinned Effect version (the
// source watch on the effect catalog line trips) or touches one of the three schemas:
//
//   bun tools/oracles/first-match-union.ts
//
// It only reads the submodule: the schemas and Effect are imported from external/opencode as the
// pinned checkout installs them (bun install in external/opencode).
import { createRequire } from "node:module"
import { readFileSync, writeFileSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"

const repository = join(dirname(fileURLToPath(import.meta.url)), "..", "..")
const upstream = join(repository, "external", "opencode")
const schemaPackage = join(upstream, "packages", "schema")
const fixtures = join(repository, "tests", "OpenCode.Sdk.Tests", "Serialization", "Fixtures")

const { Schema } = await import(createRequire(join(schemaPackage, "package.json")).resolve("effect"))
const reference = await import(join(schemaPackage, "src", "config", "reference.ts"))
const lsp = await import(join(schemaPackage, "src", "config", "lsp.ts"))
const migration = await import(join(upstream, "packages", "protocol", "src", "groups", "migration.ts"))

// Each union with its members in declaration order, the order the pinned document keeps.
const unions: Record<string, { union: unknown; members: unknown[] }> = {
  reference: { union: reference.Entry, members: reference.Entry.members },
  lsp: { union: lsp.Entry, members: lsp.Entry.members },
  migration: { union: migration.V1MigrationStatus, members: migration.V1MigrationStatus.members },
}

const corpus: { union: string; value: unknown }[] = JSON.parse(readFileSync(join(fixtures, "first-match-corpus.json"), "utf8"))
const expected = corpus.map(({ union, value }) => {
  const target = unions[union]
  if (target === undefined) throw new Error(`The corpus names an unknown union '${union}'.`)
  let decoded: unknown
  try {
    decoded = Schema.decodeUnknownSync(target.union)(value)
  } catch {
    return { union, value, member: null }
  }
  // The union's own decode chose the member; the member is the first whose type guard accepts the
  // decoded value, which carries only that member's fields.
  const member = target.members.findIndex((candidate) => Schema.is(candidate)(decoded))
  if (member < 0) throw new Error(`No member of '${union}' accepts its own decode of ${JSON.stringify(value)}.`)
  return { union, value, member }
})

writeFileSync(join(fixtures, "first-match-expected.json"), `${JSON.stringify(expected, null, 2)}\n`)
console.log(`Wrote ${expected.length} verdicts.`)
