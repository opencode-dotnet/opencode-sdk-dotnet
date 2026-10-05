// A server stand-in with a configurable process tree, for the POSIX disposal ladder proofs.
// OPENCODE_SDK_TEST_LADDER is a comma-separated list of flags:
//   group-child               a child in the stand-in's own process group
//   group-child-ignores-term  the same, ignoring SIGTERM
//   detached                  a child in a session of its own (setsid)
//   hold-stdout               a detached child that holds the stand-in's stdout open
//   root-ignores-term         the stand-in itself ignores SIGTERM
//   exit-after-ready=N        exit with code N shortly after the readiness line
//   exit-before-ready=N       exit with code N instead of reporting readiness
//   close-stdout              close stdout instead of reporting readiness, and keep running
//   kill-self-before-ready    end itself with SIGKILL instead of reporting readiness
//   exit-at-ready=N           exit with code N right after the readiness line
// Every pid is reported on stderr as "ladder-tree role=<role> pid=<pid>" before readiness, so the
// test can end each process by its pid whatever the stand-in did.
import { spawn } from "node:child_process";
import { closeSync } from "node:fs";

const flags = (process.env.OPENCODE_SDK_TEST_LADDER ?? "").split(",").filter(Boolean);
const has = (flag) => flags.includes(flag);
const value = (name) => {
  const flag = flags.find((entry) => entry.startsWith(name + "="));
  return flag === undefined ? undefined : Number(flag.slice(name.length + 1));
};
const report = (role, pid) => console.error(`ladder-tree role=${role} pid=${pid}`);

// A child reports "armed" once its handlers are installed, so no signal can arrive before them;
// the holder writes nothing, because its stdout is the stand-in's own.
const childScript =
  "if (process.env.LADDER_CHILD_IGNORE_TERM === '1') process.on('SIGTERM', () => {});" +
  "setInterval(() => {}, 1000);" +
  "if (process.env.LADDER_CHILD_REPORT === '1') process.stdout.write('armed\\n');";

async function start(role, { detached = false, ignoreTerm = false, holdStdout = false } = {}) {
  const child = spawn(process.execPath, ["-e", childScript], {
    detached,
    stdio: ["ignore", holdStdout ? "inherit" : "pipe", "ignore"],
    env: { ...process.env, LADDER_CHILD_IGNORE_TERM: ignoreTerm ? "1" : "0", LADDER_CHILD_REPORT: holdStdout ? "0" : "1" },
  });
  report(role, child.pid);
  if (!holdStdout) {
    await new Promise((resolve, reject) => {
      let seen = "";
      child.stdout.on("data", (chunk) => {
        seen += chunk;
        if (seen.includes("armed")) resolve();
      });
      child.once("error", reject);
      child.once("exit", () => reject(new Error(`${role} exited before it was armed`)));
    });
    child.stdout.destroy();
  }
  child.unref();
}

if (has("root-ignores-term")) process.on("SIGTERM", () => {});
report("root", process.pid);
if (has("group-child")) await start("group-child");
if (has("group-child-ignores-term")) await start("group-child", { ignoreTerm: true });
if (has("detached")) await start("detached", { detached: true });
if (has("hold-stdout")) await start("holder", { detached: true, holdStdout: true });

setInterval(() => {}, 1000);
const before = value("exit-before-ready");
if (before !== undefined) {
  process.exit(before);
} else if (has("close-stdout")) {
  closeSync(1);
} else if (has("kill-self-before-ready")) {
  process.kill(process.pid, "SIGKILL");
} else {
  console.log(JSON.stringify({ url: "http://127.0.0.1:1" }));
  const at = value("exit-at-ready");
  const after = value("exit-after-ready");
  if (at !== undefined) process.exit(at);
  if (after !== undefined) setTimeout(() => process.exit(after), 200);
}
