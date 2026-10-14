namespace OpenCode.Sdk.Tools.Tests.Packaging;

internal sealed class GeneratedSuppressionTargetClassifier(IReadOnlySet<string> generatedTypes)
{
    private readonly IReadOnlySet<string> _generatedTypes = generatedTypes ?? throw new ArgumentNullException(nameof(generatedTypes));

    public bool BelongsToGeneratedType(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Length < 3 || target[1] != ':' || target[0] is not ('T' or 'M' or 'P' or 'F' or 'E'))
        {
            throw new InvalidOperationException($"Suppression target '{target}' must be a type or member documentation-comment ID.");
        }

        var type = target[2..];
        if (target[0] != 'T')
        {
            var parameters = type.IndexOf('(', StringComparison.Ordinal);
            if (parameters >= 0)
            {
                type = type[..parameters];
            }

            var member = type.AsSpan().LastIndexOf('.');
            if (member <= 0)
            {
                throw new InvalidOperationException($"Suppression target '{target}' has no declaring type.");
            }

            type = type[..member];
        }

        return _generatedTypes.Any(generated => string.Equals(type, generated, StringComparison.Ordinal)
            || type.StartsWith(generated + ".", StringComparison.Ordinal));
    }
}
