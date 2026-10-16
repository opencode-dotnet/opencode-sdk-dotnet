const port = Number(process.env.OPENCODE_SDK_TEST_TREE_PORT);
const nonce = process.env.OPENCODE_SDK_TEST_TREE_NONCE;
const mode = process.env.OPENCODE_SDK_TEST_TREE_MODE;

if (!Number.isInteger(port) || port <= 0 || !nonce || !mode) {
  throw new Error("The child-tree fixture requires its port, nonce, and mode environment.");
}

// Each peer owns its own TCP connection to the test. In particular the child does not
// inherit a parent-owned pipe or a timer: killing only the root leaves this child alive.
async function connectLease() {
  const net = await import("node:net");
  const socket = net.createConnection({
    host: "127.0.0.1",
    port: Number(process.env.OPENCODE_SDK_TEST_TREE_PORT),
  });
  let finishAcknowledgement;
  const acknowledged = new Promise((resolve) => {
    finishAcknowledgement = resolve;
    let response = "";
    socket.setEncoding("utf8");
    socket.on("data", (chunk) => {
      response += chunk;
      const newline = response.indexOf("\n");
      if (newline < 0) return;
      resolve(response.slice(0, newline).trim() === "ACK");
    });
  });
  const released = new Promise((resolve) => {
    const release = () => {
      finishAcknowledgement(false);
      resolve();
    };
    socket.once("end", release);
    socket.once("close", release);
    socket.once("error", release);
  });
  await new Promise((resolve, reject) => {
    socket.once("connect", resolve);
    socket.once("error", reject);
  });
  return { socket, acknowledged, released };
}

async function runChild() {
  const lease = await connectLease();
  try {
    lease.socket.write(`${JSON.stringify({
      nonce: process.env.OPENCODE_SDK_TEST_TREE_NONCE,
      role: "child",
      pid: process.pid,
    })}\n`);
    if (!await lease.acknowledged) {
      throw new Error("The child-tree lease ended without a valid acknowledgement.");
    }
    await lease.released;
  } finally {
    lease.socket.destroy();
  }
}

// Establish the root connection first so the test can assign both roles without a
// connection-routing framework. The child cannot connect until after this succeeds.
const lease = await connectLease();
let child;
try {
  child = Bun.spawn(
    [process.execPath, "-e", `${connectLease.toString()}; await (${runChild.toString()})();`],
    { stdin: "ignore", stdout: "ignore", stderr: "ignore" },
  );
  lease.socket.write(`${JSON.stringify({
    nonce, role: "root", pid: process.pid, childPid: child.pid,
  })}\n`);
  if (!await lease.acknowledged) {
    throw new Error("The child-tree lease ended without a valid acknowledgement.");
  }

  if (mode === "invalid-line") {
    console.log("hello");
  } else if (mode !== "silent") {
    throw new Error(`Unknown child-tree fixture mode '${mode}'.`);
  }

  await lease.released;
} finally {
  lease.socket.destroy();
  // This is the root's direct, owned Bun child. Pre-ACK rejection must reap it even
  // while the independently held child lease is still open on the test side.
  if (child) {
    if (child.exitCode === null) child.kill();
    await child.exited;
  }
}
