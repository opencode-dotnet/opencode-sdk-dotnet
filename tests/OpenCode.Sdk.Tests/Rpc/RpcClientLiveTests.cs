using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Rpc;

/// <summary>
/// The rpc family's live proof against the pinned server: the repository-owned local plugin
/// exercises exact input/output plus every deterministic 400/500 RPC arm. An rpc id nobody
/// registered retains the declared <see cref="RpcError"/> unavailable proof on both error channels.
/// External mode exercises the unavailable arm, then checks inventory after the RPC activation barrier.
/// </summary>
/// <remarks>
/// No <see cref="LiveReadiness.PluginAsync"/> wait precedes the owned calls, unlike the families
/// that read <c>plugin.list</c>: the pinned <c>rpc.call</c> handler awaits plugin activation
/// server-side (<c>packages/server/src/handlers/rpc.ts:11</c> at the pin) before it looks the rpc
/// id up, so a cold location's first call cannot observe the registry before the seeded plugin
/// registers. <c>plugin.list</c> does not wait, which is why those readers poll.
/// </remarks>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class RpcClientLiveTests(PinnedOpenCodeServerFixture server)
{
    /// <summary>An rpc id no plugin at the pin registers; the server's message must name it back.</summary>
    private const string UnregisteredRpcId = "sdk-live-absent";

    private const string Method = "ping";

    /// <summary>The upstream reason for an empty registration slot (<c>core/src/rpc.ts</c>, <c>Rpc.call</c>).</summary>
    private const string UnavailableReason = "rpc.unavailable";

    private const string MissingMethodReason = "rpc.method_not_found";

    private const string InvalidInputReason = "rpc.invalid_input";

    private const string DeclaredErrorReason = "sdk.test.rejected";

    private const string EchoMethod = "echo";

    private const string MissingMethod = "missing";

    private const string DeclaredErrorMethod = "declaredError";

    private const string InvalidOutputMethod = "invalidOutput";

    private const string InternalErrorMethod = "internalError";

    private const string Nonce = "rpc-live-task-2-nonce";

    private const string EchoValue = "rpc-live-task-2-value";

    private const string DeclaredErrorCode = "sdk-test-rejected";

    private const string DeclaredErrorMessage = "SDK test RPC rejected the request.";

    private const string InternalErrorMessage = "RPC call failed";

    private readonly FixtureLoader _fixtures = new();

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Echo_The_Exact_Input_When_The_Owned_Rpc_Is_Registered(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, "echo", cancellationToken))
        {
            return;
        }

        var response = await client.Rpc.CallAsync(
            TestRpcPlugin.Id,
            EchoMethod,
            new RpcCallRequestBuilder().WithNonce(Nonce).WithValue(EchoValue).Build(),
            cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Error).IsNull();
        var output = response.Call.Output ?? throw new InvalidOperationException("The echo response had no output.");
        await Assert.That(output.GetProperty("nonce").GetString()).IsEqualTo(Nonce);
        await Assert.That(output.GetProperty("value").GetString()).IsEqualTo(EchoValue);

        Console.WriteLine("rpc-live: mode=owned arm=echo status=" + Number(response.Status) + " nonce=" + Nonce);
    }

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Answer_Method_Not_Found_When_The_Owned_Method_Is_Missing(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, "method-not-found", cancellationToken))
        {
            return;
        }

        var response = await client.Rpc.CallAsync(
            TestRpcPlugin.Id,
            MissingMethod,
            new RpcCallRequestBuilder().WithNonce(Nonce).Build(),
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        var error = await RequireRpcErrorAsync(response, 400);
        await Assert.That(error.Type).IsEqualTo(MissingMethodReason);
        await Assert.That(error.Message).IsEqualTo("Unknown RPC method: " + TestRpcPlugin.Id + "." + MissingMethod);
        await Assert.That(error.Data).IsNull();

        WriteError("method-not-found", response, error.Type);
    }

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Answer_Invalid_Input_When_The_Nonce_Is_Numeric(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, "invalid-input", cancellationToken))
        {
            return;
        }

        var response = await client.Rpc.CallAsync(
            TestRpcPlugin.Id,
            EchoMethod,
            new RpcCallRequestBuilder().WithNumericNonce(17).WithValue(EchoValue).Build(),
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        var error = await RequireRpcErrorAsync(response, 400);
        await Assert.That(error.Type).IsEqualTo(InvalidInputReason);
        await Assert.That(string.IsNullOrWhiteSpace(error.Message)).IsFalse();
        await Assert.That(error.Message).Contains("nonce");
        await Assert.That(error.Message).Contains("string");
        await Assert.That(error.Data).IsNull();

        WriteError("invalid-input", response, error.Type);
    }

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Answer_The_Declared_Error_With_Its_Typed_Data(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, "declared-error", cancellationToken))
        {
            return;
        }

        var response = await client.Rpc.CallAsync(
            TestRpcPlugin.Id,
            DeclaredErrorMethod,
            new RpcCallRequestBuilder().WithNonce(Nonce).Build(),
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        var error = await RequireRpcErrorAsync(response, 400);
        await Assert.That(error.Type).IsEqualTo(DeclaredErrorReason);
        await Assert.That(error.Message).IsEqualTo(DeclaredErrorMessage);
        var data = error.Data ?? throw new InvalidOperationException("The declared RPC error had no data.");
        await Assert.That(data.GetProperty("code").GetString()).IsEqualTo(DeclaredErrorCode);
        await Assert.That(data.GetProperty("nonce").GetString()).IsEqualTo(Nonce);

        WriteError("declared-error", response, error.Type);
    }

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Answer_Invalid_Output_When_The_Handler_Breaks_Its_Schema(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, "invalid-output", cancellationToken))
        {
            return;
        }

        var response = await client.Rpc.CallAsync(
            TestRpcPlugin.Id,
            InvalidOutputMethod,
            new RpcCallRequestBuilder().WithNonce(Nonce).Build(),
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        var error = await RequireRpcInternalErrorAsync(response, "rpc.invalid_output");
        await Assert.That(error.Type).IsEqualTo(RpcInternalErrorType.RpcInvalidOutput);
        await Assert.That(string.IsNullOrWhiteSpace(error.Message)).IsFalse();
        await Assert.That(error.Data).IsNull();

        WriteError("invalid-output", response, "rpc.invalid_output");
    }

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Normalize_A_Thrown_Handler_Error_As_Internal(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, "internal-error", cancellationToken))
        {
            return;
        }

        var response = await client.Rpc.CallAsync(
            TestRpcPlugin.Id,
            InternalErrorMethod,
            new RpcCallRequestBuilder().WithNonce(Nonce).Build(),
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        var error = await RequireRpcInternalErrorAsync(response, "rpc.internal");
        await Assert.That(error.Type).IsEqualTo(RpcInternalErrorType.RpcInternal);
        await Assert.That(error.Message).IsEqualTo(InternalErrorMessage);
        await Assert.That(error.Data).IsNull();

        WriteError("internal-error", response, "rpc.internal");
    }

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Answer_The_Unavailable_Arm_On_The_NoThrow_Spine_When_No_Rpc_Is_Registered(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        var external = await PrepareRpcModeAsync();
        var rpcId = external ? TestRpcPlugin.Id : UnregisteredRpcId;

        var response = await client.Rpc.CallAsync(
            rpcId, Method, CallRequest(), OpenCodeRequestOptions.NoThrow, cancellationToken);

        await Assert.That(response.Status).IsEqualTo(400);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Error).IsTypeOf<RpcError>();
        var error = response.Error as RpcError;
        await Assert.That(error?.Type).IsEqualTo(UnavailableReason);
        await Assert.That(error?.Message).Contains(rpcId);
        // The handler spreads `data` only when the failure carries one, and the unavailable
        // failure carries none - so the member stays absent on the wire and null here.
        await Assert.That(error?.Data).IsNull();

        // Every value is what the server answered; the raw body is the evidence a reader can
        // compare against the upstream handler without trusting this test's typed view of it.
        Console.WriteLine(
            "rpc-live: mode=" + (external ? "external" : "owned") +
            " arm=no-throw status=" + Number(response.Status) +
            " type=" + error?.Type +
            " message=" + error?.Message +
            " body=" + response.RawBody);
    }

    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Throw_The_Unavailable_Arm_When_No_Rpc_Is_Registered(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        var external = await PrepareRpcModeAsync();
        var rpcId = external ? TestRpcPlugin.Id : UnregisteredRpcId;

        var exception = await Assert
            .That(async () => _ = await client.Rpc.CallAsync(
                rpcId, Method, CallRequest(), cancellationToken: cancellationToken))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<RpcError>();
        var error = exception.Error as RpcError;
        await Assert.That(error?.Type).IsEqualTo(UnavailableReason);
        await Assert.That(error?.Message).Contains(rpcId);

        Console.WriteLine(
            "rpc-live: mode=" + (external ? "external" : "owned") +
            " arm=throw status=" + Number(exception.Status) +
            " type=" + error?.Type +
            " body=" + exception.RawBody);
    }

    /// <summary>
    /// The call body: an arbitrary JSON input the registry never gets to validate, because the
    /// rpc id lookup fails first. It is still a real body so the request the server refuses is
    /// the same shape a registered rpc would receive.
    /// </summary>
    private RpcCallRequest CallRequest()
    {
        using var document = JsonDocument.Parse(_fixtures.LoadJson("Rpc.rpc-call-input.json"));
        return new RpcCallRequest { Input = document.RootElement.Clone() };
    }

    private async Task<bool> AssertExternalUnavailableAsync(
        OpenCodeClient client,
        string requestedArm,
        CancellationToken cancellationToken)
    {
        if (!await PrepareRpcModeAsync())
        {
            return false;
        }

        var response = await client.Rpc.CallAsync(
            TestRpcPlugin.Id,
            MissingMethod,
            new RpcCallRequestBuilder().WithNonce(Nonce).Build(),
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);
        var error = await RequireRpcErrorAsync(response, 400);
        await Assert.That(error.Type).IsEqualTo(UnavailableReason);
        await Assert.That(error.Message).IsEqualTo("RPC is unavailable: " + TestRpcPlugin.Id);
        await Assert.That(error.Data).IsNull();

        var listed = await client.Plugins.ListPluginsAsync(cancellationToken: cancellationToken);
        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        await Assert.That(listed.Plugins.Where(
            static plugin => string.Equals(plugin.Id, TestRpcPlugin.Id, StringComparison.Ordinal)).ToArray()).IsEmpty();

        Console.WriteLine(
            "rpc-live: mode=external arm=rpc.unavailable requested-arm=" + requestedArm +
            " status=" + Number(response.Status));
        return true;
    }

    private async Task<bool> PrepareRpcModeAsync()
    {
        // Every owned server carries the seeded plugin, whichever build the fixture started, so a
        // missing plugin is the external endpoint and nothing else.
        if (server.RpcPlugin is not null)
        {
            return false;
        }

        await Assert.That(server.IsExternal).IsTrue();
        return true;
    }

    private static async Task<RpcError> RequireRpcErrorAsync(RpcCallResponse response, int status)
    {
        await Assert.That(response.Status).IsEqualTo(status);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Error).IsTypeOf<RpcError>();
        var error = response.Error as RpcError ?? throw new InvalidOperationException("The RPC error arm was absent.");
        var rawBody = response.RawBody ?? throw new InvalidOperationException("The RPC error had no raw body.");
        await Assert.That(rawBody).Contains(error.Tag);
        await Assert.That(rawBody).Contains(error.Type);
        return error;
    }

    private static async Task<RpcInternalError> RequireRpcInternalErrorAsync(
        RpcCallResponse response,
        string wireType)
    {
        await Assert.That(response.Status).IsEqualTo(500);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Error).IsTypeOf<RpcInternalError>();
        var error = response.Error as RpcInternalError ??
            throw new InvalidOperationException("The RPC internal-error arm was absent.");
        var rawBody = response.RawBody ?? throw new InvalidOperationException("The RPC internal error had no raw body.");
        await Assert.That(rawBody).Contains(error.Tag);
        await Assert.That(rawBody).Contains(wireType);
        return error;
    }

    private static void WriteError(string arm, RpcCallResponse response, string type) =>
        Console.WriteLine(
            "rpc-live: mode=owned arm=" + arm +
            " status=" + Number(response.Status) +
            " type=" + type +
            " body=" + response.RawBody);

    /// <summary>Renders one number for the console line, culture-free.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
