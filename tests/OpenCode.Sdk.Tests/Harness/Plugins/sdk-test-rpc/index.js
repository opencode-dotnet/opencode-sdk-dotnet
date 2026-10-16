const nonceSchema = {
  type: "object",
  properties: { nonce: { type: "string" } },
  required: ["nonce"],
  additionalProperties: false,
}

const echoSchema = {
  type: "object",
  properties: {
    nonce: { type: "string" },
    value: { type: "string" },
  },
  required: ["nonce", "value"],
  additionalProperties: false,
}

const okSchema = {
  type: "object",
  properties: { ok: { type: "boolean" } },
  required: ["ok"],
  additionalProperties: false,
}

const stringValueSchema = {
  type: "object",
  properties: { value: { type: "string" } },
  required: ["value"],
  additionalProperties: false,
}

const rejectedDataSchema = {
  type: "object",
  properties: {
    code: { type: "string" },
    nonce: { type: "string" },
  },
  required: ["code", "nonce"],
  additionalProperties: false,
}

export default {
  id: "opencode.sdk.test.rpc",
  setup: async (host) => {
    let registration
    registration = await host.rpc.register(
      {
        id: "opencode.sdk.test.rpc",
        methods: {
          echo: { input: echoSchema, output: echoSchema },
          emit: { input: nonceSchema, output: nonceSchema },
          declaredError: {
            input: nonceSchema,
            output: okSchema,
            errors: { "sdk.test.rejected": rejectedDataSchema },
          },
          invalidOutput: { input: nonceSchema, output: stringValueSchema },
          internalError: { input: nonceSchema, output: okSchema },
        },
        events: {
          observed: { schema: nonceSchema },
        },
      },
      {
        echo: async (input) => input,
        emit: async (input) => {
          await registration.events.emit("observed", { nonce: input.nonce })
          return { nonce: input.nonce }
        },
        declaredError: async (input, context) =>
          context.error(
            "sdk.test.rejected",
            "SDK test RPC rejected the request.",
            { code: "sdk-test-rejected", nonce: input.nonce },
          ),
        invalidOutput: async () => ({ value: 42 }),
        internalError: async () => {
          throw new Error("SDK test RPC internal failure.")
        },
      },
    )
  },
}
