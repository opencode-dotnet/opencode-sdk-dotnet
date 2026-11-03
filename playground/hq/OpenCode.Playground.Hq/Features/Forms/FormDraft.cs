using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Forms;

/// <summary>A person's answers to one form, built into the reply the server takes.</summary>
internal sealed class FormDraft(FormInfo form)
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _choices = new(StringComparer.Ordinal);

    public FormInfo Form => form;
    public bool Answerable => form.Fields.All(static item => item is FormStringField or FormMultiselectField);
    public static bool ShowsTextBox(FormStringField field) => field.Options is null or { Count: 0 } || field.Custom == true;
    public string TextOf(string key) => _texts.GetValueOrDefault(key, string.Empty);
    public string TextClass(string key, string value) => TextOf(key) == value ? "chosen" : string.Empty;
    public bool IsChosen(string key, string value) => _choices.TryGetValue(key, out var chosen) && chosen.Contains(value);
    public void SetText(string key, string? text) => _texts[key] = text ?? string.Empty;

    public void Toggle(string key, string value)
    {
        if (!_choices.TryGetValue(key, out var chosen))
        {
            chosen = new HashSet<string>(StringComparer.Ordinal);
            _choices[key] = chosen;
        }

        if (!chosen.Add(value))
        {
            _ = chosen.Remove(value);
        }
    }

    /// <summary>Builds a text per text question and chosen values in option order per multiselect.</summary>
    public SessionFormReplyRequest ToReply() => new()
    {
        Answer = form.Fields.Select(field => field switch
        {
            FormStringField text => KeyValuePair.Create(text.Key, FormValue.FromText(TextOf(text.Key))),
            FormMultiselectField multi => KeyValuePair.Create(multi.Key, FormValue.FromTextList(
                [.. multi.Options.Select(static option => option.Value).Where(value => IsChosen(multi.Key, value))])),
            _ => throw new InvalidOperationException("HQ answers text and choice questions only."),
        }).ToDictionary(StringComparer.Ordinal),
    };
}
