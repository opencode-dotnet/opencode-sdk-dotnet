// The parity oracle for the trailing-wildcard route rule (fs.read).
//
// It calls upstream's own generated Promise client (packages/client/src/promise/generated/client.ts)
// with a recording fetch, once per path of tests/OpenCode.Sdk.Tests/FileSystem/Fixtures/
// wildcard-path-corpus.json, and writes the request path the client produced to
// wildcard-path-expected.json beside it. The SDK's route test asserts the generated route builder
// produces the same path. Re-run it when a refresh moves the watched codegen rule
// (packages/httpapi-codegen/src/index.ts, promiseWildcardInput and encodePath):
//
//   bun tools/oracles/wildcard-path.ts
//
// It only reads the submodule. Dot segments stay out of the corpus: the client's URL parser
// resolves them away, and the SDK refuses them by name instead.
import { readFileSync, writeFileSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"

const repository = join(dirname(fileURLToPath(import.meta.url)), "..", "..")
const upstream = join(repository, "external", "opencode")
const fixtures = join(repository, "tests", "OpenCode.Sdk.Tests", "FileSystem", "Fixtures")
const { make } = await import(join(upstream, "packages", "client", "src", "promise", "generated", "client.ts"))

const corpus: string[] = JSON.parse(readFileSync(join(fixtures, "wildcard-path-corpus.json"), "utf8"))
const expected: { path: string; route: string }[] = []
for (const path of corpus) {
  let requested: URL | undefined
  const client = make({
    baseUrl: "http://127.0.0.1:4096",
    fetch: async (input: URL) => {
      requested = new URL(input)
      return new Response(new Uint8Array(), { status: 200, headers: { "content-type": "application/octet-stream" } })
    },
  })
  await client.file.read({ path })
  if (requested === undefined) throw new Error(`The client sent no request for '${path}'.`)
  expected.push({ path, route: requested.pathname })
}

writeFileSync(join(fixtures, "wildcard-path-expected.json"), `${JSON.stringify(expected, null, 2)}\n`)
console.log(`Wrote ${expected.length} routes.`)
