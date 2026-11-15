using System.Net;
using System.Text.Json;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Rpc.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Rpc;

public sealed class RpcClientExtensionsTests
{
    [Test]
    public async Task CallAsync_Should_Send_The_Typed_Input_And_Location_And_Read_The_Output()
    {
        using var scenario = EchoScenario();

        var output = await scenario.Client.Rpc.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput,
            new LocationSelector { Directory = RpcFacadeData.Directory });

        await Assert.That(output?.Nonce).IsEqualTo(RpcFacadeData.OutputNonce);
        await Assert.That(output?.Value).IsEqualTo(RpcFacadeData.OutputValue);
        await Assert.That(scenario.Requests).Count().IsEqualTo(1);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri?.AbsoluteUri).IsEqualTo(RpcFacadeData.EchoUri);
        await Assert.That(request.Body).IsEqualTo(RpcFacadeData.EchoInputBody);
        await Assert.That(request.Headers.Keys).DoesNotContain("x-opencode-directory");
    }

    [Test]
    [Arguments("typed-null")]
    [Arguments("json-null")]
    public async Task CallAsync_Should_Send_Json_Null_For_A_Null_Input(string inputKey)
    {
        using var scenario = EchoScenario();
        var input = inputKey == "typed-null" ? (JsonElement?)null : RpcFacadeData.Element(RpcFacadeData.JsonNull);

        _ = await scenario.Client.Rpc.CallAsync(RpcFacadeData.RawEcho, input);

        await Assert.That(scenario.Requests.Single().Body).IsEqualTo(RpcFacadeData.NullInputBody);
    }

    [Test]
    [Arguments(nameof(RpcFacadeData.AbsentOutputBody), "echo")]
    [Arguments(nameof(RpcFacadeData.NullOutputBody), "echo")]
    [Arguments(nameof(RpcFacadeData.AbsentOutputBody), "count")]
    [Arguments(nameof(RpcFacadeData.NullOutputBody), "count")]
    public async Task CallAsync_Should_Read_Null_Outputs_With_The_Output_Metadata(string bodyKey, string descriptor)
    {
        var body = bodyKey == nameof(RpcFacadeData.AbsentOutputBody) ? RpcFacadeData.AbsentOutputBody : RpcFacadeData.NullOutputBody;
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, body);

        if (descriptor == "echo")
        {
            await Assert.That(await scenario.Client.Rpc.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput)).IsNull();
        }
        else
        {
            await AssertOutputFailureAsync(() => scenario.Client.Rpc.CallAsync(RpcFacadeData.Count, RpcFacadeData.EchoInput));
        }
    }

    [Test]
    public async Task CallAsync_Should_Convert_An_Unreadable_Output_To_A_Transport_Failure()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, RpcFacadeData.UnreadableOutputBody);

        await AssertOutputFailureAsync(() => scenario.Client.Rpc.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput));
    }

    [Test]
    [Arguments(400, RpcFacadeData.RejectionBody, RpcFacadeData.RejectedType, RpcFacadeData.RejectionData)]
    [Arguments(500, RpcFacadeData.InternalErrorBody, RpcFacadeData.InvalidOutputType, null)]
    [Arguments(400, RpcFacadeData.UnavailableBody, RpcFacadeData.UnavailableType, null)]
    public async Task CallAsync_Should_Unwrap_The_Rpc_Failure(int status, string body, string errorType, string? data)
    {
        var failure = await CallFailureAsync<OpenCodeRpcException>(status, body)
            ?? throw new InvalidOperationException("The RPC exception was absent.");

        await Assert.That(failure.ErrorType).IsEqualTo(errorType);
        await Assert.That(failure.Message).IsEqualTo(RpcFacadeData.Element(body).GetProperty("message").GetString());
        await Assert.That(failure.ErrorData?.GetRawText()).IsEqualTo(data);
        await Assert.That(failure.Status).IsEqualTo(status);
        await Assert.That(failure.Error?.GetType()).IsEqualTo(status == 400 ? typeof(RpcError) : typeof(RpcInternalError));
        await Assert.That(failure.RawBody).IsEqualTo(body);
    }

    [Test]
    [Arguments(400, WireBodyData.InvalidRequestError)]
    [Arguments(401, WireBodyData.UnauthorizedError)]
    [Arguments(400, RpcFacadeData.UnparsedErrorBody)]
    [Arguments(401, RpcFacadeData.TaggedRejectionBody)]
    public async Task CallAsync_Should_Preserve_Non_Rpc_Api_Failures(int status, string body)
    {
        var failure = await CallFailureAsync<OpenCodeApiException>(status, body);

        await Assert.That(failure?.Status).IsEqualTo(status);
        if (body == RpcFacadeData.TaggedRejectionBody)
        {
            await Assert.That(failure?.Error).IsTypeOf<UnknownOpenCodeError>();
            await Assert.That((failure?.Error as UnknownOpenCodeError)?.Tag).IsEqualTo(RpcFacadeData.RpcErrorTag);
        }
        if (body == RpcFacadeData.UnparsedErrorBody)
        {
            await Assert.That(failure?.Error).IsNull();
        }
    }

    [Test]
    public async Task CallAsync_Should_Propagate_Transport_Failures()
    {
        using var scenario = ContractScenario.Responding(static _ => throw new HttpRequestException(RpcFacadeData.RejectedMessage));

        await Assert.That(() => scenario.Client.Rpc.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput))
            .ThrowsExactly<OpenCodeTransportException>();
    }

    [Test]
    [Arguments("untyped")]
    [Arguments("downgraded")]
    [Arguments("transport")]
    public async Task CallAsync_Should_Preserve_The_Non_Rpc_Failure_Instance(string failureKey)
    {
        Exception expected = failureKey switch
        {
            "untyped" => new OpenCodeApiException(RpcFacadeData.RejectedMessage, 400, null, RpcFacadeData.UnparsedErrorBody),
            "downgraded" => new OpenCodeApiException(RpcFacadeData.RejectedMessage, 401,
                new UnknownOpenCodeError(RpcFacadeData.RpcErrorTag, RpcFacadeData.Element(RpcFacadeData.TaggedRejectionBody)),
                RpcFacadeData.TaggedRejectionBody),
            "transport" => new OpenCodeTransportException(RpcFacadeData.RejectedMessage),
            _ => throw new InvalidOperationException(failureKey),
        };
        var client = FailingDoor(expected);

        var failure = await Assert.That(() => client.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput)).Throws<Exception>();

        await Assert.That(failure).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task CallAsync_Should_Refuse_NoThrow_Before_A_Request()
    {
        using var scenario = EchoScenario();

        var failure = await Assert.That(() => scenario.Client.Rpc.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput,
            requestOptions: OpenCodeRequestOptions.NoThrow)).ThrowsExactly<ArgumentException>();

        await Assert.That(failure?.ParamName).IsEqualTo("requestOptions");
        const string message = "Typed RPC calls always throw API errors; NoThrow is not supported.";
        // ArgumentException renders its parameter suffix differently on .NET Framework.
#if NETFRAMEWORK
        await Assert.That(failure?.Message).IsEqualTo(message + "\r\nParameter name: requestOptions");
#else
        await Assert.That(failure?.Message).IsEqualTo(message + " (Parameter 'requestOptions')");
#endif
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    [Arguments(true, false, false, "client")]
    [Arguments(false, true, false, "method")]
    [Arguments(true, true, false, "client")]
    [Arguments(false, true, true, "method")]
    public async Task CallAsync_Should_Guard_Arguments_In_Order_Before_A_Request(
        bool nullClient, bool nullMethod, bool noThrow, string parameter)
    {
        using var scenario = EchoScenario();
        // Null-forgiving operators deliberately supply invalid arguments to the extension's guards.
        var client = nullClient ? null! : scenario.Client.Rpc;
        var method = nullMethod ? null! : RpcFacadeData.Echo;

        var failure = await Assert.That(() => client.CallAsync(method, RpcFacadeData.EchoInput,
            requestOptions: noThrow ? OpenCodeRequestOptions.NoThrow : null)).ThrowsExactly<ArgumentNullException>();

        await Assert.That(failure?.ParamName).IsEqualTo(parameter);
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    public async Task CallAsync_Should_Forward_Every_Argument_To_The_Virtual_Door()
    {
        var client = Substitute.For<RpcClient>();
        client.CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RpcCallRequest>(), Arg.Any<OpenCodeRequestOptions>(),
            Arg.Any<CancellationToken>()).Returns(new RpcCallResponse
            {
                Status = 200,
                Call = new RpcOutput { Output = RpcFacadeData.Element(RpcFacadeData.EchoOutputValue) },
            });
        using var caller = new CancellationTokenSource();
        var location = new LocationSelector { Directory = RpcFacadeData.Directory };
        var options = new OpenCodeRequestOptions { NetworkTimeout = TimeSpan.FromSeconds(7) };

        var output = await client.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput, location, options, caller.Token);

        await Assert.That(output?.Nonce).IsEqualTo(RpcFacadeData.OutputNonce);
        await Assert.That(output?.Value).IsEqualTo(RpcFacadeData.OutputValue);
        var calls = client.ReceivedCalls().ToArray();
        await Assert.That(calls).Count().IsEqualTo(1);
        var arguments = calls.Single().GetArguments();
        await Assert.That(arguments[0]).IsEqualTo(TestRpcPlugin.Id);
        await Assert.That(arguments[1]).IsEqualTo(RpcFacadeData.EchoName);
        var request = arguments[2] as RpcCallRequest ?? throw new InvalidOperationException("The RPC request was absent.");
        await Assert.That(request.Location).IsSameReferenceAs(location);
        await Assert.That(request.Input?.GetRawText()).IsEqualTo(RpcFacadeData.EchoInputValue);
        await Assert.That(arguments[3]).IsSameReferenceAs(options);
        await Assert.That(arguments[4]).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task CallAsync_Should_Preserve_The_Cancellation_Instance()
    {
        using var caller = new CancellationTokenSource();
        OperationCanceledException? expected = null;
        var client = Substitute.For<RpcClient>();
        client.CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RpcCallRequest>(), Arg.Any<OpenCodeRequestOptions>(),
            Arg.Any<CancellationToken>()).Returns<Task<RpcCallResponse>>(async call =>
            {
                await caller.CancelOnWorkerAsync();
                expected = new OperationCanceledException(call.Arg<CancellationToken>());
                throw expected;
            });

        var failure = await Assert.That(() => client.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput,
            cancellationToken: caller.Token)).Throws<OperationCanceledException>();

        await Assert.That(failure).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task CallAsync_Should_Use_The_Descriptor_Input_And_Output_Metadata()
    {
        using var scenario = EchoScenario();

        var output = await scenario.Client.Rpc.CallAsync(RpcFacadeData.WireEcho, RpcFacadeData.EchoInput);

        await Assert.That(scenario.Requests.Single().Body).IsEqualTo(RpcFacadeData.EchoWireInputBody);
        await Assert.That(output?.Nonce).IsEqualTo(RpcFacadeData.OutputNonce);
        await Assert.That(output?.Value).IsEqualTo(RpcFacadeData.OutputValue);
    }

    [Test]
    public async Task CallAsync_Should_Preserve_A_Non_Json_Metadata_Failure()
    {
        using var scenario = EchoScenario();

        var failure = await Assert.That(() => scenario.Client.Rpc.CallAsync(RpcFacadeData.FailingEcho, RpcFacadeData.EchoInput))
            .Throws<InvalidOperationException>();

        await Assert.That(failure).IsSameReferenceAs(RpcFacadeData.MetadataFailure);
    }

    private static ContractScenario EchoScenario() => ContractScenario.Responding(HttpStatusCode.OK, RpcFacadeData.EchoOutputBody);

    private static async Task<TException?> CallFailureAsync<TException>(int status, string body) where TException : Exception
    {
        using var scenario = ContractScenario.Responding((HttpStatusCode)status, body);
        return await Assert.That(() => scenario.Client.Rpc.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput))
            .ThrowsExactly<TException>();
    }

    private static RpcClient FailingDoor(Exception failure)
    {
        var client = Substitute.For<RpcClient>();
        client.CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RpcCallRequest>(), Arg.Any<OpenCodeRequestOptions>(),
            Arg.Any<CancellationToken>()).Returns(Task.FromException<RpcCallResponse>(failure));
        return client;
    }

    private static async Task AssertOutputFailureAsync(Func<Task> call)
    {
        var failure = await Assert.That(call).ThrowsExactly<OpenCodeTransportException>();
        await Assert.That(failure?.Message).IsEqualTo(RpcFacadeData.OutputFailureMessage);
        await Assert.That(failure?.InnerException).IsTypeOf<JsonException>();
    }
}
