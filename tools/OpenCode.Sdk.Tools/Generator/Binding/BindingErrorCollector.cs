using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class BindingErrorCollector
{
    private readonly List<BindingError> _errors = [];

    public int Count => _errors.Count;

    public IReadOnlyList<BindingError> Snapshot() => Array.AsReadOnly([.. _errors]);

    public void Append(IEnumerable<BindingError> errors) => _errors.AddRange(errors);

    public BindingError Add(BindingErrorCategory category, string subject, string problem, BindingErrorKind kind = BindingErrorKind.Unspecified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        var error = new BindingError(category, subject, problem) { Kind = kind };
        _errors.Add(error);
        return error;
    }

    public void ThrowIfAny()
    {
        if (_errors.Count > 0)
        {
            throw new BindingException(_errors);
        }
    }
}
