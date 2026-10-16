namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>A way the repository lowers, silences or switches off a diagnostic, as the suppression ledger names it.</summary>
internal enum SuppressionChannel
{
    /// <summary>A <c>#pragma warning disable</c> directive.</summary>
    Pragma,

    /// <summary>A <c>#nullable disable</c> directive, which turns nullable warnings off from that line on.</summary>
    NullableDirective,

    /// <summary>A <c>[SuppressMessage]</c> or <c>[UnconditionalSuppressMessage]</c> attribute.</summary>
    SuppressMessage,

    /// <summary>A <c>[SlopwatchSuppress]</c> attribute.</summary>
    SlopwatchSuppress,

    /// <summary>A slopwatch-ignore comment, in C# or in a project file.</summary>
    SlopwatchIgnore,

    /// <summary>An entry in <c>.slopwatch/baseline.json</c>.</summary>
    SlopwatchBaseline,

    /// <summary>An <c>.editorconfig</c> entry that lowers a severity, marks code generated, or configures a rule.</summary>
    EditorConfig,

    /// <summary>A global analyzer config entry of the same kinds.</summary>
    GlobalConfig,

    /// <summary>A diagnostic ID in a <c>NoWarn</c> property.</summary>
    NoWarn,

    /// <summary>A diagnostic ID in a <c>WarningsNotAsErrors</c> property.</summary>
    WarningsNotAsErrors,

    /// <summary>A diagnostic ID in an <c>MSBuildWarningsAsMessages</c> property.</summary>
    MSBuildWarningsAsMessages,

    /// <summary>A leading auto-generated comment outside the generated manifest.</summary>
    GeneratedCodeHeader,

    /// <summary>A <c>GeneratedCode</c> attribute outside the generated manifest.</summary>
    GeneratedCodeAttribute,

    /// <summary>
    /// An assignment of a build property that switches the analyzer wall, its severity or nullable
    /// analysis as a whole, in an MSBuild file, a file-based app directive or a workflow's command line.
    /// </summary>
    BuildSwitch,
}
