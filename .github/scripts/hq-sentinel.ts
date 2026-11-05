// The consumer leg's HQ check. HQ renders only over its live connection, so a plain GET of its page
// carries no board; this reads the rendered page through headless Chrome's DevTools protocol until
// the page's text contains the marker, or fails with the text it saw.
// Usage: bun .github/scripts/hq-sentinel.ts <devtools-port> <url> <marker> <timeout-ms>
const [port, url, marker, timeoutArg] = process.argv.slice(2)
const timeout = Number(timeoutArg)
const started = Date.now()
let text = ""
// One deadline over every step, a socket or reply that never comes included.
setTimeout(() => {
  console.error(`HQ never showed ${marker} within ${timeout} ms. Its page read: ${text.slice(0, 500)}`)
  process.exit(1)
}, timeout)
const targets = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()) as { type: string; webSocketDebuggerUrl: string }[]
const page = targets.find((target) => target.type === "page")
if (!page) throw new Error("Chrome exposes no page target.")

const socket = new WebSocket(page.webSocketDebuggerUrl)
await new Promise((resolve, reject) => {
  socket.onopen = resolve
  socket.onerror = reject
})
// One command is in flight at a time, so a reply is matched to it by id; events carry no id.
let next = 0
let awaiting: { id: number; resolve: (message: any) => void } | undefined
socket.onmessage = (event) => {
  const message = JSON.parse(String(event.data))
  if (awaiting && message.id === awaiting.id) awaiting.resolve(message)
}
const send = (method: string, params: object = {}) =>
  new Promise<any>((resolve) => {
    const id = ++next
    awaiting = { id, resolve }
    socket.send(JSON.stringify({ id, method, params }))
  })

await send("Page.navigate", { url })
for (;;) {
  const reply = await send("Runtime.evaluate", { expression: "document.body ? document.body.innerText : ''", returnByValue: true })
  text = String(reply.result?.result?.value ?? "")
  if (text.includes(marker)) {
    console.log(`HQ shows ${marker} after ${Date.now() - started} ms.`)
    process.exit(0)
  }
  await Bun.sleep(250)
}
