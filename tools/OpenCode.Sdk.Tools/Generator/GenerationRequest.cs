namespace OpenCode.Sdk.Tools.Generator;

internal sealed record GenerationRequest
{
    public required string SpecPath { get; init; }

    public required string ProfilePath { get; init; }

    public required string CurationPath { get; init; }

    public required string OutputRoot { get; init; }

    public required string ProjectPath { get; init; }

    public required bool Verify { get; init; }

    public static GenerationRequest RepositoryDefault(bool verify) =>
        new()
        {
            SpecPath = Refresh.SnapshotPaths.AcceptedDocument,
            ProfilePath = "tools/generation-profile.txt",
            CurationPath = "tools/curation.json",
            OutputRoot = "src/OpenCode.Sdk",
            ProjectPath = "src/OpenCode.Sdk/OpenCode.Sdk.csproj",
            Verify = verify,
        };
}
