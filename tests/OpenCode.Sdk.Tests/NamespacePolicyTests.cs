using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The SDK's namespace rule (<see cref="SdkNamespacePolicy"/>) against the real source tree, which
/// it must accept and whose allow-lists it must still need, and against small synthetic files that
/// each keep or break one part of the rule.
/// </summary>
public sealed class NamespacePolicyTests
{
    private const string PublicNamespace = "OpenCode.Sdk";

    private const string ModelsNamespace = "OpenCode.Sdk.Models";

    private const string PublicFolderFile = "Sessions/Widget.cs";

    private const string ModelsFolderFile = "Models/Widget.cs";

    private const string InternalFolderFile = "Sessions/Internal/Widget.cs";

    private const string NonLegacyInternalFolder = "Internal/Transport";

    private const string TypeName = "Widget";

    private const string CompanionTypeName = "WidgetCompanion";

    private const string Public = "public";

    private const string Internal = "internal";

    [Test]
    public async Task SdkSource_Should_Declare_The_Namespace_And_Accessibility_Its_Folder_Requires()
    {
        var files = await new SdkSourceTree().LoadAsync();

        var violations = files.Select(SdkNamespacePolicy.Violation).OfType<string>().ToArray();

        await Assert.That(files).IsNotEmpty();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task LegacyNamespaceFolders_Should_Each_Still_Hold_A_File_With_A_Legacy_Namespace()
    {
        var stale = SdkNamespacePolicy.StaleLegacyNamespaceFolders(await new SdkSourceTree().LoadAsync());

        await Assert.That(stale).IsEmpty();
    }

    [Test]
    [Arguments(PublicFolderFile, PublicNamespace)]
    [Arguments(ModelsFolderFile, ModelsNamespace)]
    public async Task Violation_Should_Admit_A_Public_Folder_File_When_It_Declares_A_Public_Namespace_And_Public_Types(
        string relativePath,
        string declaredNamespace)
    {
        var file = SourceFile(relativePath, declaredNamespace, Declaration(Public, TypeName), Declaration(Public, CompanionTypeName));

        await Assert.That(SdkNamespacePolicy.Violation(file)).IsNull();
    }

    [Test]
    public async Task Violation_Should_Admit_An_Internal_Folder_File_When_It_Declares_The_Internal_Namespace_And_No_Public_Type()
    {
        var file = SourceFile(InternalFolderFile, SdkNamespacePolicy.InternalNamespace, Declaration(Internal, TypeName));

        await Assert.That(SdkNamespacePolicy.Violation(file)).IsNull();
    }

    [Test]
    [Arguments(InternalFolderFile, SdkNamespacePolicy.InternalNamespace, Public)]
    [Arguments(InternalFolderFile, PublicNamespace, Internal)]
    [Arguments(PublicFolderFile, SdkNamespacePolicy.InternalNamespace, Public)]
    public async Task Violation_Should_Refuse_A_File_When_Its_Namespace_Or_Accessibility_Departs_From_Its_Folder(
        string relativePath,
        string declaredNamespace,
        string accessibility)
    {
        var file = SourceFile(relativePath, declaredNamespace, Declaration(accessibility, TypeName));

        await Assert.That(SdkNamespacePolicy.Violation(file)).IsNotNull();
    }

    [Test]
    public async Task Violation_Should_Refuse_A_Public_Folder_File_When_A_Later_Top_Level_Type_Is_Not_Public()
    {
        var file = SourceFile(PublicFolderFile, PublicNamespace, Declaration(Public, TypeName), Declaration(Internal, CompanionTypeName));

        await Assert.That(SdkNamespacePolicy.Violation(file)).IsNotNull();
    }

    [Test]
    public async Task Violation_Should_Refuse_A_File_When_It_Declares_No_Namespace()
    {
        var file = new SdkSourceFile(PublicFolderFile, [Declaration(Public, TypeName)]);

        await Assert.That(SdkNamespacePolicy.Violation(file)).IsNotNull();
    }

    [Test]
    public async Task Violation_Should_Admit_A_Folder_Namespace_When_The_Folder_Is_Legacy()
    {
        var file = FolderNamespaceFile(SdkNamespacePolicy.LegacyNamespaceFolders[0]);

        await Assert.That(SdkNamespacePolicy.Violation(file)).IsNull();
    }

    [Test]
    public async Task Violation_Should_Refuse_A_Folder_Namespace_When_The_Internal_Folder_Is_Not_Legacy()
    {
        var file = FolderNamespaceFile(NonLegacyInternalFolder);

        await Assert.That(SdkNamespacePolicy.Violation(file)).IsNotNull();
    }

    /// <summary>An internal file under <paramref name="folder"/> that declares the namespace derived from that folder.</summary>
    private static SdkSourceFile FolderNamespaceFile(string folder) => SourceFile(
        $"{folder}/{TypeName}.cs",
        $"{PublicNamespace}.{folder.Replace('/', '.')}",
        Declaration(Internal, TypeName));

    private static SdkSourceFile SourceFile(string relativePath, string declaredNamespace, params string[] declarations) =>
        new(relativePath, [NamespaceLine(declaredNamespace), .. declarations]);

    private static string NamespaceLine(string declaredNamespace) => $"namespace {declaredNamespace};";

    private static string Declaration(string accessibility, string typeName) => $"{accessibility} sealed class {typeName}";
}
