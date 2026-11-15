using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class CiDocumentationRunTests
{
    [Test]
    public async Task Ci_Should_Classify_Markdown_As_Docs_On_Every_Event()
    {
        var fileSystem = new RealFileSystem();
        var workflow = await new CiWorkflow(fileSystem).ReadAsync(new RepositoryRoot(fileSystem).Locate());
        var ignored = workflow.Split('\n').SkipWhile(static line => line != "    paths-ignore:").Skip(1)
            .TakeWhile(static line => line.StartsWith("      - ", StringComparison.Ordinal));
        await Assert.That(ignored).IsEquivalentTo(["      - LICENSE"], CollectionOrdering.Matching);
        var changes = CiWorkflow.Job(workflow, "changes");
        await Assert.That(changes).Contains("      docs: ${{ steps.diff.outputs.docs }}\n");
        await Assert.That(changes).Contains("      matrix: ${{ steps.diff.outputs.matrix }}\n");
        await Assert.That(changes).Contains("          BEFORE: ${{ github.event.before }}\n");
        await Assert.That(changes).Contains("              *.md) docs=true ;;\n");
        await Assert.That(changes).Contains("          docs=false\n");
        await Assert.That(changes).Contains("          echo \"docs=$docs\" >> \"$GITHUB_OUTPUT\"\n");
        await Assert.That(changes).Contains("""
                      push_fallback() {
                        echo "code=true" >> "$GITHUB_OUTPUT"
                        echo "benchmarks=true" >> "$GITHUB_OUTPUT"
                        echo "benchmarks-generator=true" >> "$GITHUB_OUTPUT"
                        echo "docs=false" >> "$GITHUB_OUTPUT"
                        echo "matrix=$matrix" >> "$GITHUB_OUTPUT"
                        exit 0
                      }
            """);
        await Assert.That(changes).Contains("      - name: Checkout\n        uses: actions/checkout@v7\n        with:\n          fetch-depth: 2\n");
        await Assert.That(changes).Contains("""
                      if [ "$EVENT" = push ]; then
                        if [ -z "$BEFORE" ] || [[ "$BEFORE" =~ ^0+$ ]] || ! git fetch --no-tags --depth=1 origin "$BEFORE"; then
                          push_fallback
                        fi
                        base="$BEFORE"
                      elif [ "$EVENT" = merge_group ]; then
            """);
        await Assert.That(changes).Contains("""
                      if ! git diff --name-only --no-renames -z "$base" HEAD > "$RUNNER_TEMP/changed-paths"; then
                        [ "$EVENT" = push ] && push_fallback
                        exit 1
                      fi
            """);
        await Assert.That(changes).Contains("""
                      if [ "$EVENT" = push ] && [ "$code" = true ]; then
                        benchmarks=true
                        benchmarks_generator=true
                      fi
            """);
    }

    [Test]
    public async Task Ci_Should_Select_Only_The_Linux_Leg_For_A_Docs_Only_Change()
    {
        var fileSystem = new RealFileSystem();
        var workflow = await new CiWorkflow(fileSystem).ReadAsync(new RepositoryRoot(fileSystem).Locate());
        var build = CiWorkflow.Job(workflow, "build-and-test");
        await Assert.That(build).Contains("    if: ${{ !cancelled() && needs.changes.result == 'success' && (needs.changes.outputs.code == 'true' || needs.changes.outputs.docs == 'true') && !(needs.reuse.result == 'success' && needs.reuse.outputs.reuse == 'true') }}\n");
        await Assert.That(build).Contains("        include: ${{ fromJSON(needs.changes.outputs.matrix) }}\n");
        await Assert.That(build).Contains("      fail-fast: false\n");
        var changes = CiWorkflow.Job(workflow, "changes");
        await Assert.That(changes).Contains("          matrix='[{\"os\":\"windows-2025\",\"name\":\"Windows\",\"test-modules\":4},{\"os\":\"ubuntu-24.04\",\"name\":\"Linux\",\"test-modules\":4},{\"os\":\"macos-15\",\"name\":\"macOS\",\"test-modules\":3}]'\n");
        await Assert.That(changes).Contains("""
                      if [ "$code" = false ]; then
                        matrix='[{"os":"ubuntu-24.04","name":"Linux","test-modules":4}]'
                      fi
            """);
        await Assert.That(changes).Contains("          echo \"matrix=$matrix\" >> \"$GITHUB_OUTPUT\"\n");
    }

    [Test]
    public async Task Ci_Should_Require_The_Linux_Leg_For_A_Docs_Only_Result()
    {
        var fileSystem = new RealFileSystem();
        var workflow = await new CiWorkflow(fileSystem).ReadAsync(new RepositoryRoot(fileSystem).Locate());
        var aggregate = CiWorkflow.Job(workflow, "ci-result");
        await Assert.That(aggregate).Contains("          DOCS: ${{ needs.changes.outputs.docs }}\n");
        await Assert.That(aggregate).Contains("          echo \"changes=$CHANGES code=$CODE docs=$DOCS lint=$LINT build-and-test=$BUILD_AND_TEST\"\n");
        await Assert.That(aggregate).Contains("""
                      if [ "$CODE" = true ]; then
                        [ "$LINT" = success ] && [ "$BUILD_AND_TEST" = success ] || exit 1
                      elif [ "$CODE" = false ] && [ "$DOCS" = true ]; then
                        [ "$LINT" = skipped ] && [ "$BUILD_AND_TEST" = success ] || exit 1
                      else
                        [ "$CODE" = false ] && [ "$DOCS" = false ] || exit 1
                      fi
            """);
    }
}
