namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal abstract record FacetResult<T>
{
    private FacetResult() { }

    public sealed record Bound(T Value) : FacetResult<T>;

    public sealed record Refused : FacetResult<T>
    {
        public Refused(IReadOnlyList<BindingError> errors)
        {
            ArgumentNullException.ThrowIfNull(errors);
            if (errors.Count is 0)
            {
                throw new ArgumentException("A refused facet must carry its reason.", nameof(errors));
            }
            Errors = Array.AsReadOnly([.. errors]);
        }

        public IReadOnlyList<BindingError> Errors { get; }
    }
}
