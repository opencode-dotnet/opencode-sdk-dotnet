// A server stand-in whose descendant keeps the shared stdout pipe open after the stand-in itself
// has gone. The holder inherits stdout, is detached so the stand-in's exit does not take it along,
// and records its pid for the test's own cleanup; the stand-in then reports readiness and exits on
// stdin EOF, the launcher's lease release.
import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";

const pidFile = process.env.OPENCODE_SDK_TEST_HOLDER_PID_FILE;
if (!pidFile) {
  throw new Error("The stdout-holder fixture requires OPENCODE_SDK_TEST_HOLDER_PID_FILE.");
}

const holder = spawn(process.execPath, ["-e", "setTimeout(() => {}, 120000)"], {
  detached: true,
  stdio: ["ignore", "inherit", "ignore"],
  windowsHide: true,
});
holder.unref();
writeFileSync(pidFile, String(holder.pid));

console.log(JSON.stringify({ url: "http://127.0.0.1:1" }));
process.stdin.resume();
process.stdin.on("end", () => process.exit(0));
