using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Rpc.Support;

namespace OpenCode.Sdk.Tests.Rpc;

public sealed class OpenCodeRpcExceptionTests
{
    [Test]
    public async Task OpenCodeRpcException_Should_Preserve_The_Rpc_And_Http_Failure()
    {
        var data = RpcFacadeData.Element(RpcFacadeData.RejectionData);
        var error = new RpcError { Type = RpcFacadeData.RejectedType, Message = RpcFacadeData.RejectedMessage, Data = data };

        var exception = new OpenCodeRpcException(error.Type, error.Message, data, 400, error, RpcFacadeData.TaggedRejectionBody);

        await Assert.That(exception.ErrorType).IsEqualTo(RpcFacadeData.RejectedType);
        await Assert.That(exception.Message).IsEqualTo(RpcFacadeData.RejectedMessage);
        await Assert.That(exception.ErrorData?.GetRawText()).IsEqualTo(RpcFacadeData.RejectionData);
        await Assert.That(exception.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsSameReferenceAs(error);
        await Assert.That(exception.RawBody).IsEqualTo(RpcFacadeData.TaggedRejectionBody);
        await Assert.That(exception).IsAssignableTo<OpenCodeApiException>();
    }

    [Test]
    [Arguments("none")]
    [Arguments("message")]
    [Arguments("message-and-inner")]
    public async Task OpenCodeRpcException_Should_Initialize_The_Standard_Constructors(string constructor)
    {
        var inner = new InvalidOperationException(RpcFacadeData.RejectedMessage);
        var exception = constructor switch
        {
            "none" => new OpenCodeRpcException(),
            "message" => new OpenCodeRpcException(RpcFacadeData.RejectedMessage),
            "message-and-inner" => new OpenCodeRpcException(RpcFacadeData.RejectedMessage, inner),
            _ => throw new InvalidOperationException(constructor),
        };

        await Assert.That(exception.ErrorType).IsEqualTo(string.Empty);
        await Assert.That(exception.ErrorData).IsNull();
        await Assert.That(exception.Status).IsEqualTo(0);
        if (constructor == "none")
        {
            await Assert.That(exception.Message).Contains(typeof(OpenCodeRpcException).FullName!);
        }
        else
        {
            await Assert.That(exception.Message).IsEqualTo(RpcFacadeData.RejectedMessage);
        }
        await Assert.That(exception.InnerException).IsSameReferenceAs(constructor == "message-and-inner" ? inner : null);
    }

    [Test]
    public async Task OpenCodeRpcException_Should_Refuse_A_Null_Type()
    {
        // The null type deliberately challenges the public constructor's guard.
        var exception = await Assert.That(() => new OpenCodeRpcException(null!, RpcFacadeData.RejectedMessage, null, 400, null, null))
            .ThrowsExactly<ArgumentNullException>();

        await Assert.That(exception?.ParamName).IsEqualTo("type");
    }
}
