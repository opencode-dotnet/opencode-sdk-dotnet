namespace OpenCode.Sdk.Tools.Tests.Packaging;

internal sealed class PackageVersionStepClassifier
{
    private readonly Version _current;
    private readonly Version? _baseline;

    public PackageVersionStepClassifier(string versionPrefix, string baselineVersion)
    {
        _current = Parse(versionPrefix);
        _baseline = string.IsNullOrWhiteSpace(baselineVersion) ? null : Parse(baselineVersion);
    }

    public PackageVersionStep Classify()
    {
        if (_baseline is null)
        {
            return PackageVersionStep.NoBaseline;
        }

        if (_current.Major > _baseline.Major)
        {
            return PackageVersionStep.Major;
        }

        if (_current.Major == _baseline.Major && _current.Minor > _baseline.Minor)
        {
            return PackageVersionStep.Minor;
        }

        if (_current.Major == _baseline.Major && _current.Minor == _baseline.Minor)
        {
            return PackageVersionStep.Patch;
        }

        throw new InvalidOperationException($"VersionPrefix '{_current}' is below baseline '{_baseline}'.");
    }

    private static Version Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var numeric = value.Split('-', 2)[0];
        return Version.TryParse(numeric, out var version) && version.Build >= 0 && version.Revision < 0
            ? version
            : throw new InvalidOperationException($"Package version '{value}' must have major.minor.patch components.");
    }
}
