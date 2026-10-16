namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The test mirror rule (<see cref="TestMirror"/>) against the real source tree, which must have no
/// collision, and against small synthetic trees that show what collides and where a test belongs.
/// </summary>
public sealed class TestMirrorTests
{
    private const string PublicFile = "Sessions/Widget.cs";

    [Test]
    public async Task SdkSource_Should_Project_Each_Type_Onto_Its_Own_Test_Path()
    {
        var files = await new SdkSourceTree().LoadAsync();

        var collisions = TestMirror.Collisions(files);

        await Assert.That(files).IsNotEmpty();
        await Assert.That(collisions).IsEmpty();
    }

    [Test]
    public async Task Collisions_Should_Name_Both_Subjects_When_A_Public_And_An_Internal_File_Share_A_Type_Name()
    {
        var collisions = TestMirror.Collisions([SourceFile(PublicFile), SourceFile("Sessions/Internal/Widget.cs")]);

        await Assert.That(collisions).IsEquivalentTo(["Sessions/Internal/Widget and Sessions/Widget"]);
    }

    [Test]
    public async Task Collisions_Should_Be_Empty_When_Partial_Parts_Of_One_Type_Share_A_Folder()
    {
        var collisions = TestMirror.Collisions([SourceFile(PublicFile), SourceFile("Sessions/Widget.Unix.cs")]);

        await Assert.That(collisions).IsEmpty();
    }

    [Test]
    [Arguments("Internal/Transport/Pipeline.cs", "Transport/PipelineTests.cs")]
    [Arguments(PublicFile, "Sessions/WidgetTests.cs")]
    public async Task TestPath_Should_Mirror_The_Source_Path_Without_Internal_Segments(string relativePath, string expectedTestPath)
    {
        var testPath = TestMirror.TestPath(SourceFile(relativePath));

        await Assert.That(testPath).IsEqualTo(expectedTestPath);
    }

    private static SdkSourceFile SourceFile(string relativePath) => new(relativePath, []);
}
