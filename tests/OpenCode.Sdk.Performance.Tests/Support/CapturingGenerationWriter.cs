#if NET
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Output.Abstractions;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>Retains the coordinator's write request while performing the real write.</summary>
internal sealed class CapturingGenerationWriter(GenerationWriter writer) : IGenerationWriter
{
    private readonly GenerationWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public GenerationWriteRequest? Request { get; private set; }

    public Task<WriteResult> WriteAsync(GenerationWriteRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        return _writer.WriteAsync(request, cancellationToken);
    }
}
#endif
