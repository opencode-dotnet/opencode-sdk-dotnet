namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>Restricts accepted API breaks to the surface the version step permits.</summary>
internal sealed class CompatibilitySuppressionClassifier
{
    private readonly PackageCompatibilityEvidence _evidence;
    private readonly GeneratedSuppressionTargetClassifier _targets;

    public CompatibilitySuppressionClassifier(PackageCompatibilityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        _evidence = evidence;
        _targets = new GeneratedSuppressionTargetClassifier(evidence.GeneratedTypes);
    }

    public IReadOnlyList<string> Validate()
    {
        var step = new PackageVersionStepClassifier(_evidence.VersionPrefix, _evidence.BaselineVersion).Classify();
        var violations = _evidence.HandWrittenPartials
            .Where(partial => _evidence.GeneratedTypes.Contains(partial.Value))
            .Select(partial => $"Hand-written file '{partial.Key}' declares partial generated type '{partial.Value}'; its members cannot be classified.")
            .ToList();
        foreach (var project in _evidence.ProjectSuppressions)
        {
            foreach (var suppression in project.Value)
            {
                var reason = Classify(step, project.Key, suppression);
                if (reason is not null)
                {
                    violations.Add($"{project.Key}: {suppression.DiagnosticId} '{suppression.Target}': {reason}");
                }
            }
        }

        return violations;
    }

    public string? Classify(PackageVersionStep step, string project, CompatibilitySuppression suppression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(suppression);

        if (!suppression.IsBaselineSuppression)
        {
            return "cross-asset suppressions are forbidden; every asset must expose the same API.";
        }

        var generatedTarget = _targets.BelongsToGeneratedType(suppression.Target);
        return step switch
        {
            PackageVersionStep.NoBaseline => "no baseline is configured; no suppressions are allowed.",
            PackageVersionStep.Patch => "a patch step permits no suppressions.",
            PackageVersionStep.Minor when !string.Equals(project, "OpenCode.Sdk", StringComparison.Ordinal)
                || !generatedTarget => "this hand-written break needs a major version step.",
            PackageVersionStep.Minor or PackageVersionStep.Major => null,
            _ => throw new InvalidOperationException($"Unknown version step '{step}'."),
        };
    }
}
