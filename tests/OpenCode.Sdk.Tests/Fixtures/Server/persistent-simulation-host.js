import { NodeServices } from "@effect/platform-node";
import { LayerNode } from "@opencode/util/effect/layer-node";
import { Global } from "@opencode/util/global";
import { Observability } from "@opencode/util/observability";
import { AppProcess } from "@opencode/util/process";
import { start } from "@opencode/server/process";
import { Effect } from "effect";
import { HttpServer } from "effect/unstable/http";

const password = process.env.OPENCODE_PASSWORD ?? process.env.OPENCODE_SERVER_PASSWORD;
if (!password) throw new Error("Missing server password");

// Match the CLI's stdio credential boundary: tools spawned by the server never inherit its lease.
delete process.env.OPENCODE_PASSWORD;
delete process.env.OPENCODE_SERVER_PASSWORD;

const truthy = (value) => value === "1" || value?.toLowerCase() === "true";

const waitForStdinClose = Effect.callback((resume) => {
  const close = () => resume(Effect.void);
  process.stdin.once("end", close);
  process.stdin.once("close", close);
  process.stdin.resume();
  if (process.stdin.readableEnded || process.stdin.destroyed) close();
  return Effect.sync(() => {
    process.stdin.off("end", close);
    process.stdin.off("close", close);
    process.stdin.pause();
  });
});

const application = Effect.scoped(
  Effect.gen(function* () {
    yield* Effect.logInfo("persistent simulation host starting");
    const server = yield* start({
      app: { name: "cli", version: "local", channel: "local" },
      hostname: "127.0.0.1",
      port: 0,
      password,
      simulation: true,
      database: { path: process.env.OPENCODE_DB ?? "opencode-local.db" },
      events: { persist: true },
      models: {
        url: process.env.OPENCODE_MODELS_URL,
        file: process.env.OPENCODE_MODELS_PATH,
        fetch: !truthy(process.env.OPENCODE_DISABLE_MODELS_FETCH),
      },
      config: {
        directory: process.env.OPENCODE_CONFIG_DIR,
        project: !truthy(
          process.env.OPENCODE_CONFIG_PROJECT_DISABLE ?? process.env.OPENCODE_DISABLE_PROJECT_CONFIG,
        ),
        file: process.env.OPENCODE_CONFIG,
        content: process.env.OPENCODE_CONFIG_CONTENT,
      },
      windows: { gitbash: process.env.OPENCODE_GIT_BASH_PATH },
      fs: {
        filewatcher: !truthy(process.env.OPENCODE_FILEWATCHER_DISABLE ?? process.env.OPENCODE_DISABLE_FILEWATCHER),
        fff:
          process.env.OPENCODE_DISABLE_FFF === undefined
            ? process.platform !== "win32"
            : !truthy(process.env.OPENCODE_DISABLE_FFF),
      },
    });
    const url = HttpServer.formatAddress(server.address);
    yield* Effect.logWarning("persistent simulation host ready", { url });
    console.log(JSON.stringify({ url }));
    yield* waitForStdinClose;
    yield* Effect.logInfo("persistent simulation host stdin closed");
  }),
).pipe(
  Effect.provide(LayerNode.compile(LayerNode.group([Global.node, AppProcess.node]))),
  Effect.provide(Observability.layer({ client: "cli", version: "local", channel: "local" })),
  Effect.provide(NodeServices.layer),
);

await Effect.runPromise(application);
