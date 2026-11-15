// The parity oracle for the CLI's background-service configuration operations.
//
// It runs upstream's ServiceConfig.read/get/set/unset over service-config-corpus.json in
// tests/OpenCode.Sdk.Tests/Hosting/BackgroundService/Config/Fixtures and writes
// service-config-expected.json beside it. Each cors-origin-corpus.json item runs through
// ServiceConfig.set("cors", origin), producing cors-origin-expected.json in the same folder.
// Re-run when a refresh moves cli/src/services/service-config.ts, util/src/global.ts,
// or the pinned Effect:
//
//   bun tools/oracles/service-config.ts
//
// It only reads the submodule. All nine Global roots are contained in one fresh system-temp
// scenario root, removed in finally even when the scenario fails. No real profile is acquired.
import { createRequire } from "node:module"
import { mkdtempSync, readFileSync, writeFileSync, rmSync, existsSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"

const repository = join(dirname(fileURLToPath(import.meta.url)), "..", "..")
const upstream = join(repository, "external", "opencode")
const require = createRequire(join(upstream, "packages", "cli", "package.json"))
const { Effect, Exit, Cause, Schema, Option } = await import(require.resolve("effect"))
const { NodeFileSystem } = await import(require.resolve("@effect/platform-node"))
const { Global } = await import(join(upstream, "packages", "util", "src", "global.ts"))
const { ServiceConfig } = await import(join(upstream, "packages", "cli", "src", "services", "service-config.ts"))
const fixtures = join(repository, "tests", "OpenCode.Sdk.Tests", "Hosting", "BackgroundService", "Config", "Fixtures")
const decodeCurrent = Schema.decodeUnknownOption(Schema.fromJsonString(ServiceConfig.Info))
const decodeLegacy = Schema.decodeUnknownOption(
  Schema.fromJsonString(Schema.Struct({ ...ServiceConfig.Info.fields, remote: Schema.Boolean })),
)

type Scenario = { name: string; initial: string | null; command: string[]; divergence?: string }

function operation(command: string[]) {
  const [verb, ...args] = command
  switch (verb) {
    case "get": return ServiceConfig.get(args[0], args[1])
    case "read": return ServiceConfig.read()
    case "set": return ServiceConfig.set(args[0], args[1], args[2])
    case "unset": return ServiceConfig.unset(args[0], args[1])
    default: throw new Error(`Unknown corpus verb: ${verb}`)
  }
}

// Only generated files need masking, and upstream writes them in canonical indented form.
// Anchoring at the top-level member prevents a matching env name/value from being masked.
function maskGenerated(file: string, member: "route" | "password", value: string) {
  const prefix = member === "route" ? '\n  "remote": {\n    "route": ' : '\n  "password": '
  const needle = prefix + JSON.stringify(value)
  if (!file.includes(needle)) throw new Error(`Generated ${member} has no structural location in the file`)
  return file.replace(needle, prefix + JSON.stringify(`@${member}`))
}

async function run(scenario: Scenario) {
  const root = mkdtempSync(join(tmpdir(), "opencode-service-config-oracle-"))
  const roots = Object.fromEntries(
    ["home", "data", "cache", "config", "state", "tmp", "bin", "log", "repos"].map((name) => [name, join(root, name)]),
  )
  try {
    // Acquiring this layer, rather than the default Global layer, creates only contained roots.
    const layer = Global.layerWith(roots)
    await Effect.runPromise(Effect.void.pipe(Effect.provide(layer)))
    const path = join(roots.config, ServiceConfig.filename())
    if (scenario.initial !== null) writeFileSync(path, scenario.initial)
    const exit = await Effect.runPromise(
      operation(scenario.command).pipe(Effect.provide(layer), Effect.provide(NodeFileSystem.layer), Effect.exit),
    )
    let file = existsSync(path) ? readFileSync(path, "utf8") : null
    let output = Exit.isSuccess(exit) && scenario.command[0] === "get" ? String(exit.value) : null
    if (Exit.isSuccess(exit) && file !== null) {
      const text = new TextDecoder().decode(new TextEncoder().encode(scenario.initial ?? "{}"))
      const initial = Option.getOrUndefined(decodeCurrent(text)) ?? Option.getOrUndefined(decodeLegacy(text)) ?? {}
      if (initial.remote === true) {
        const route = JSON.parse(file).remote?.route
        if (typeof route === "string") file = maskGenerated(file, "route", route)
      }
      if (scenario.command[0] === "get" && scenario.command[1] === "password" && !initial.password) {
        file = maskGenerated(file, "password", JSON.parse(file).password)
        output = "@password"
      }
    }
    const failure = Exit.isFailure(exit) ? Cause.squash(exit.cause) : null
    return {
      name: scenario.name,
      outcome: Exit.isSuccess(exit) ? "ok" : "error",
      message: failure === null ? null : failure instanceof Error ? failure.message : String(failure),
      output,
      file,
    }
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const corpus: Scenario[] = JSON.parse(readFileSync(join(fixtures, "service-config-corpus.json"), "utf8"))
const expected = []
for (const scenario of corpus) expected.push(await run(scenario))
writeFileSync(join(fixtures, "service-config-expected.json"), JSON.stringify(expected, null, 2) + "\n")

const origins: string[] = JSON.parse(readFileSync(join(fixtures, "cors-origin-corpus.json"), "utf8"))
const cors = []
for (const origin of origins) {
  const result = await run({ name: origin, initial: "{}", command: ["set", "cors", origin] })
  cors.push({ origin, valid: result.outcome === "ok" })
}
writeFileSync(join(fixtures, "cors-origin-expected.json"), JSON.stringify(cors, null, 2) + "\n")
console.log(`Wrote ${expected.length} configuration scenarios and ${cors.length} CORS verdicts; all scenario roots removed.`)
