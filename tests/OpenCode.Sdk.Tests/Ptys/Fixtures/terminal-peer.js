const readline = await import("node:readline");

const input = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
  terminal: false,
});

input.on("line", (line) => {
  // Readiness is requested after attachment, so the PTY host already owns the output listener.
  // A leading delimiter keeps the response distinct from echoed input and terminal controls.
  if (line === "READY?") process.stdout.write("\nREADY\n");
  const match = /^RUN ([0-9a-f]+)$/.exec(line);
  if (match) process.stdout.write(`\nACK:${match[1]}\n`);
});

await new Promise((resolve) => input.once("close", resolve));
