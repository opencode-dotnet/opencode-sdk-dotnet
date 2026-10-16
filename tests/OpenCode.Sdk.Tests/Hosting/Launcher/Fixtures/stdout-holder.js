// A server stand-in whose descendant keeps the shared stdout pipe open after the stand-in itself
// has gone. The holder inherits stdout and is detached, so neither the stand-in's exit, a signal
// to its group, nor a job the stand-in runs in takes it along. A short-lived intermediate starts
// the holder, records its pid for the test's own cleanup, and exits before the stand-in reports
// readiness, so the holder's parent is gone and a tree kill, which finds descendants by their
// parent, does not reach it either. The stand-in then exits on stdin EOF, the launcher's lease
// release.
import { spawn } from "node:child_process";

if (!process.env.OPENCODE_SDK_TEST_HOLDER_PID_FILE) {
  throw new Error("The stdout-holder fixture requires OPENCODE_SDK_TEST_HOLDER_PID_FILE.");
}

const startHolder =
  "const holder = require('node:child_process').spawn(process.execPath, ['-e', 'setTimeout(() => {}, 120000)'], " +
  "{ detached: true, stdio: ['ignore', 'inherit', 'ignore'], windowsHide: true });" +
  "holder.unref();" +
  "require('node:fs').writeFileSync(process.env.OPENCODE_SDK_TEST_HOLDER_PID_FILE, String(holder.pid));";

const intermediate = spawn(process.execPath, ["-e", startHolder], {
  stdio: ["ignore", "inherit", "inherit"],
  windowsHide: true,
});
intermediate.once("exit", (code, signal) => {
  if (code !== 0) {
    console.error(`stdout-holder: the intermediate ended with code ${code} signal ${signal}`);
    process.exit(1);
  }

  console.log(JSON.stringify({ url: "http://127.0.0.1:1" }));
});

process.stdin.resume();
process.stdin.on("end", () => process.exit(0));
