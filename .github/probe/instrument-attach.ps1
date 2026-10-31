# Throwaway probe for #724, never for merge: logs each step of tool.attach on the runner's copy of
# the simulated provider, and samples the machine beside the test pass.
param([Parameter(Mandatory)] [string] $Workspace)

$results = Join-Path $Workspace 'test-results/main'
New-Item -ItemType Directory -Force $results | Out-Null
$log = (Join-Path $results 'attach-probe.log').Replace('\', '/')
$file = Join-Path $Workspace 'external/opencode/packages/simulation/src/backend/simulated-provider.ts'
$text = [IO.File]::ReadAllText($file)

$helper = @'
import { appendFileSync } from "node:fs"
let __overlay = 0
const __p = (message: string) => appendFileSync("LOG_PATH", `${new Date().toISOString()} ${process.pid} ${message}\n`)

'@.Replace('LOG_PATH', $log)

$edits = @(
    @('const token = {}', 'const token = {}; const __id = ++__overlay; __p(`overlay ${__id} start ctx=${Object.keys(ctx).join(",")}`)'),
    @('const nextScope = yield* Scope.fork(scope)', '__p(`overlay ${__id} reconcile enter gen=${generation} n=${nextRegistrations.length}`); const nextScope = yield* Scope.fork(scope); __p(`overlay ${__id} scope forked gen=${generation}`)'),
    @('const previousScope = currentScope', '__p(`overlay ${__id} transform done gen=${generation} ok=${!Exit.isFailure(applied)}`); const previousScope = currentScope'),
    @('if (previousScope) yield* Scope.close(previousScope, Exit.void)', '__p(`overlay ${__id} closing previous gen=${generation} has=${previousScope !== undefined}`); if (previousScope) yield* Scope.close(previousScope, Exit.void); __p(`overlay ${__id} reconcile exit gen=${generation}`)'),
    @('const reconciliation = current.reconciliation.get(token)', 'const reconciliation = current.reconciliation.get(token); __p(`overlay ${__id} ack gen=${generation} pending=${reconciliation?.generation}`)'),
    @('const pending = reconciliation.get(token)', 'const pending = reconciliation.get(token); __p(`overlay ${__id} finalizer pending=${pending?.generation}`)'),
    @('yield* reconcile(initialized.generation, initialized.registrations)', '__p(`overlay ${__id} initial gen=${initialized.generation}`); yield* reconcile(initialized.generation, initialized.registrations); __p(`overlay ${__id} initial done`)'),
    @('const result = yield* Effect.exit(reconcile(update.generation, update.registrations))', '__p(`overlay ${__id} update taken gen=${update.generation}`); const result = yield* Effect.exit(reconcile(update.generation, update.registrations))'),
    @('yield* PubSub.publish(registrationUpdates, { generation, registrations })', '__p(`attach publish gen=${generation} overlays=${reconciliation.size} n=${registrations.length}`); yield* PubSub.publish(registrationUpdates, { generation, registrations })'),
    @('const failure = results.find(Exit.isFailure)', '__p(`attach acks complete gen=${reconciliation.generation}`); const failure = results.find(Exit.isFailure)')
)

foreach ($edit in $edits) {
    $count = ([regex]::Matches($text, [regex]::Escape($edit[0]))).Count
    if ($count -ne 1) { throw "Expected one match for '$($edit[0])', found $count." }
    $text = $text.Replace($edit[0], $edit[1])
}

[IO.File]::WriteAllText($file, $helper + $text)

"Instrumented $file; logging to $log."
