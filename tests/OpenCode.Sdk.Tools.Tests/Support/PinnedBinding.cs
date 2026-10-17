using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>The pinned inputs and bound plan shared for read-only use in one test session.</summary>
public sealed class PinnedBinding : IAsyncInitializer
{
    private const string NotInitialized = "PinnedBinding is not initialized. Let TUnit initialize the session fixture before reading its state.";

    private SpecDocument? _document;
    private OperationSelection? _selection;
    private GenerationCuration? _curation;
    private EmitPlan? _plan;

    internal SpecDocument Document => _document ?? throw new InvalidOperationException(NotInitialized);

    internal OperationSelection Selection => _selection ?? throw new InvalidOperationException(NotInitialized);

    internal GenerationCuration Curation => _curation ?? throw new InvalidOperationException(NotInitialized);

    internal EmitPlan Plan => _plan ?? throw new InvalidOperationException(NotInitialized);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // TUnit initialization outlives any one test and has no per-test cancellation token.
        var (document, selection, curation) = await BindingTestHost.LoadPinnedInputsAsync();
        var plan = new BindingTestHost().Bind(document, selection, curation);

        _document = document;
        _selection = selection;
        _curation = curation;
        _plan = plan;
    }
}
