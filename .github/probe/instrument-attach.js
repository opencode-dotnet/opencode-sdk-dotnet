// #724 probe: logs each step of tool.attach in this working copy only. Never merges.
import { readFileSync, writeFileSync } from "node:fs"

const [file, log] = process.argv.slice(2)
let text = readFileSync(file, "utf8")
if (text.includes("__overlay")) {
  console.log("already instrumented")
  process.exit(0)
}

const edits = [
  ['const token = {}', 'const token = {}; const __id = ++__overlay; __p(`overlay ${__id} start`)'],
  ['const nextScope = yield* Scope.fork(scope)', '__p(`overlay ${__id} reconcile enter gen=${generation} n=${nextRegistrations.length}`); const nextScope = yield* Scope.fork(scope)'],
  ['const previousScope = currentScope', '__p(`overlay ${__id} transform done gen=${generation} ok=${!Exit.isFailure(applied)}`); const previousScope = currentScope'],
  ['if (previousScope) yield* Scope.close(previousScope, Exit.void)', '__p(`overlay ${__id} closing previous gen=${generation} has=${previousScope !== undefined}`); if (previousScope) yield* Scope.close(previousScope, Exit.void); __p(`overlay ${__id} reconcile exit gen=${generation}`)'],
  ['const reconciliation = current.reconciliation.get(token)', 'const reconciliation = current.reconciliation.get(token); __p(`overlay ${__id} ack gen=${generation} pending=${reconciliation?.generation}`)'],
  ['const pending = reconciliation.get(token)', 'const pending = reconciliation.get(token); __p(`overlay ${__id} finalizer pending=${pending?.generation}`)'],
  ['yield* reconcile(initialized.generation, initialized.registrations)', '__p(`overlay ${__id} initial gen=${initialized.generation}`); yield* reconcile(initialized.generation, initialized.registrations); __p(`overlay ${__id} initial done`)'],
  ['const result = yield* Effect.exit(reconcile(update.generation, update.registrations))', '__p(`overlay ${__id} update taken gen=${update.generation}`); const result = yield* Effect.exit(reconcile(update.generation, update.registrations))'],
  ['yield* PubSub.publish(registrationUpdates, { generation, registrations })', '__p(`attach publish gen=${generation} overlays=${reconciliation.size} n=${registrations.length}`); yield* PubSub.publish(registrationUpdates, { generation, registrations })'],
  ['const failure = results.find(Exit.isFailure)', '__p(`attach acks complete gen=${reconciliation.generation}`); const failure = results.find(Exit.isFailure)'],
]

for (const [find, replace] of edits) {
  const count = text.split(find).length - 1
  if (count !== 1) throw new Error(`Expected one match for '${find}', found ${count}.`)
  text = text.replace(find, () => replace)
}

const helper = `import { appendFileSync } from "node:fs"
let __overlay = 0
const __p = (message: string) => appendFileSync(${JSON.stringify(log)}, \`\${new Date().toISOString()} \${process.pid} \${message}\\n\`)

`
writeFileSync(file, helper + text)
new Bun.Transpiler({ loader: "ts" }).transformSync(helper + text)
console.log(`instrumented ${file}`)
