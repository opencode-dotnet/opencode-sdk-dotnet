using Microsoft.AspNetCore.Components;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Forms;

/// <summary>One question form, answered in place.</summary>
public sealed partial class FormCard : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>OnParametersSet creates the draft before Blazor renders.</summary>
    private FormDraft _draft = null!;

    /// <summary>Gets or sets the form to answer.</summary>
    /// <remarks>Each caller supplies Form before Blazor runs the parameter lifecycle.</remarks>
    [Parameter]
    public FormInfo Form { get; set; } = null!;

    /// <inheritdoc />
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    protected override void OnParametersSet() => _draft ??= new FormDraft(Form);

    private Task AnswerAsync() => Board.AnswerFormAsync(Form, _draft.ToReply(), _lifetime.Token);
}
