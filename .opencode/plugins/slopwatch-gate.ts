// Runs Slopwatch after every file-mutating tool call and appends its verdict to the tool result, so
// the model sees it on its next turn. It reports and never blocks: the edit stays written, and the
// completion gate and CI stay the enforcement.
import { Plugin } from "@opencode/plugin"
import { execFile } from "node:child_process"

// The direct tools that change files.
const MUTATING = new Set(["edit", "write", "patch"])

// Resolves to the text the model must see, or undefined when the tree is clean. Never rejects: the
// host treats a rejected execute.after as a defect, not as a tool error.
function slopwatch(directory: string): Promise<string | undefined> {
  return new Promise((resolve) => {
    execFile(
      "dotnet",
      ["tool", "run", "slopwatch", "analyze", "-d", directory, "--hook"],
      { cwd: directory, timeout: 120_000, windowsHide: true, maxBuffer: 4 * 1024 * 1024 },
      (error, _stdout, stderr) => {
        if (!error) return resolve(undefined)
        const code = (error as NodeJS.ErrnoException & { code?: number | string }).code
        if (code === 2) return resolve(stderr.trim())
        resolve(`SLOPWATCH could not run (${String(code ?? error.message)}): ${stderr.trim() || error.message}`)
      },
    )
  })
}

export default Plugin.define({
  id: "repo.slopwatch-gate",
  setup: async (ctx) => {
    await ctx.tool.hook("execute.after", async (event) => {
      if (event.status !== "completed" || !MUTATING.has(event.tool)) return
      const verdict = await slopwatch(ctx.location.directory)
      if (verdict === undefined) return
      const note = `\n\n${verdict}`
      const content = event.result.content
      event.result = {
        ...event.result,
        content:
          typeof content === "string"
            ? content + note
            : [
                ...(content?.length ? content : [{ type: "text" as const, text: JSON.stringify(event.result.output) }]),
                { type: "text" as const, text: note },
              ],
      }
    })
  },
})
